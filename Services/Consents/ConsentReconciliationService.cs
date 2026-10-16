using System.Globalization;
using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Consents;

/// <summary>One document of <c>GET /v1/subjects/{ref}/consent-status</c> (<c>DocumentConsentStatus</c>).</summary>
public sealed record RemoteDocumentConsent(string Document, string? LastAction, int? GrantedVersion);

/// <summary>
/// Daily comparison of local consent records with the service (Consents module Shadow or On):
/// for every user with a record from the last <see cref="Lookback"/>, reads
/// <c>GET /v1/subjects/{ref}/consent-status</c> and logs differences as warnings. It only
/// reports — the gate always reads the local records — and it never runs in a request.
/// Users whose consent batch is still in the outbox are skipped; records never queued (made while
/// Off and not yet backfilled) show up as missing in the service. One instance runs it at a time
/// (lease <see cref="LeaseName"/> in <c>BackofficeStates</c>, which works through a transaction
/// pooler, unlike the session advisory lock used before #79); the last run is kept there too.
/// </summary>
public sealed class ConsentReconciliationService : BackgroundService
{
    public const string HttpClientName = "P4Backoffice.Consents";

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(7);

    /// <summary>How often the service wakes up to see whether a run is due.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

    /// <summary>Safety stop for one run.</summary>
    public const int MaxSubjectsPerRun = 2000;

    /// <summary>Lease that makes one run exclusive across instances (<c>lease:consents-reconcile</c>).</summary>
    public const string LeaseName = "consents-reconcile";

    /// <summary>A lease not renewed for this long is free again; renewed every third of it while the run lasts.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _httpClients;
    private readonly IOptions<P4BackofficeProductOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ConsentReconciliationService> _logger;

    public ConsentReconciliationService(
        IServiceScopeFactory scopes,
        IHttpClientFactory httpClients,
        IOptions<P4BackofficeProductOptions> options,
        TimeProvider time,
        ILogger<ConsentReconciliationService> logger)
    {
        _scopes = scopes;
        _httpClients = httpClients;
        _options = options;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never hold up application start
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[consents] reconciliation failed; next try in {Delay}", CheckEvery);
            }

            try
            {
                await Task.Delay(CheckEvery, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs when the last run is older than <see cref="Interval"/>; returns the number of subjects with differences, or null when skipped.</summary>
    public async Task<int?> RunIfDueAsync(CancellationToken cancellationToken)
    {
        await using var lease = await BackofficeLease.TryAcquireAsync(_scopes, LeaseName, LeaseDuration, _time, _logger, cancellationToken);
        if (lease == null) return null;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lost);
        return await RunLockedAsync(db, run.Token);
    }

    private async Task<int?> RunLockedAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var last = await BackofficeStateStore.GetAsync(db, BackofficeStateKeys.ConsentsReconciledAt, cancellationToken);
        if (DateTimeOffset.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastRun)
            && now - lastRun < Interval)
        {
            return null;
        }

        var differing = await ReconcileAsync(db, now, cancellationToken);
        await BackofficeStateStore.SetAsync(db, BackofficeStateKeys.ConsentsReconciledAt, now.ToString("O"), now, cancellationToken);
        return differing;
    }

    /// <summary>Requests of one run that go to the service at the same time.</summary>
    public const int Parallelism = 4;

    /// <summary>This many failures in a row (no success between) mean the service is down: the run stops.</summary>
    public const int StopAfterConsecutiveFailures = 5;

    /// <summary>
    /// One pass over the recent subjects, newest first; returns how many differ. A subject whose
    /// check fails is logged and skipped. Throws only when the service answered nothing at all
    /// (every check failed), so a partial run still counts as a run.
    /// </summary>
    internal async Task<int> ReconcileAsync(ApplicationDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = now - Lookback;
        var userIds = await db.ConsentRecords.AsNoTracking()
            // Not filtered by OutboxItemId: records sent by ConsentBackfill keep it null (append-only
            // table), and those users are the ones most likely to differ. Unsent batches are skipped below.
            .Where(c => c.OccurredAt >= since)
            .GroupBy(c => c.UserId)
            .OrderByDescending(g => g.Max(c => c.OccurredAt))
            .Select(g => g.Key)
            .Take(MaxSubjectsPerRun)
            .ToListAsync(cancellationToken);

        var requirements = ConsentRequirements.From(_options.Value.Consents);
        var work = new List<(string SubjectRef, IReadOnlyList<LatestConsent> Local)>();
        foreach (var userId in userIds)
        {
            var subjectRef = ConsentRequirements.SubjectRef(userId);
            var pending = await db.BackofficeOutboxItems.AsNoTracking()
                .AnyAsync(i => i.Kind == ConsentRequirements.OutboxKind
                               && i.SubjectRef == subjectRef
                               && i.Status != BackofficeOutboxStatus.Sent, cancellationToken);
            if (pending) continue;
            work.Add((subjectRef, await ConsentStatusProvider.LatestAsync(db, userId, cancellationToken)));
        }

        var http = _httpClients.CreateClient(HttpClientName);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(Parallelism);
        var differing = 0;
        var succeeded = 0;
        var failed = 0;
        var failuresInARow = 0;
        Exception? lastError = null;
        await Task.WhenAll(work.Select(async item =>
        {
            try { await slots.WaitAsync(stop.Token); }
            catch (OperationCanceledException) { return; }
            try
            {
                var remote = await FetchAsync(http, item.SubjectRef, stop.Token);
                var differences = Compare(requirements, item.Local, remote);
                Interlocked.Increment(ref succeeded);
                Interlocked.Exchange(ref failuresInARow, 0);
                if (differences.Count > 0)
                {
                    Interlocked.Increment(ref differing);
                    _logger.LogWarning("[consents] {SubjectRef} differs from the service: {Differences}",
                        item.SubjectRef, string.Join("; ", differences));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The run was stopped because the service is down.
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
                Interlocked.Increment(ref failed);
                _logger.LogWarning(ex, "[consents] check of {SubjectRef} failed", item.SubjectRef);
                if (Interlocked.Increment(ref failuresInARow) >= StopAfterConsecutiveFailures) stop.Cancel();
            }
            finally { slots.Release(); }
        }));
        cancellationToken.ThrowIfCancellationRequested();

        if (succeeded == 0 && failed > 0)
        {
            throw new HttpRequestException("Consent reconciliation: every check failed", lastError);
        }
        _logger.LogInformation("[consents] reconciliation: {Subjects} subjects checked, {Differing} differ, {Failed} failed",
            succeeded, differing, failed);
        return differing;
    }

    private static async Task<IReadOnlyList<RemoteDocumentConsent>> FetchAsync(HttpClient http, string subjectRef, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"v1/subjects/{Uri.EscapeDataString(subjectRef)}/consent-status", cancellationToken);
        response.EnsureSuccessStatusCode();
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var dto = await JsonSerializer.DeserializeAsync<StatusDto>(body, Json, cancellationToken);
            return (dto?.Documents ?? [])
                .Where(d => !string.IsNullOrEmpty(d.Document))
                .Select(d => new RemoteDocumentConsent(d.Document!, d.LastAction, d.GrantedVersion))
                .ToList();
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("Consent status response is not valid JSON", ex);
        }
    }

    /// <summary>Differences between the newest local record and the service, per required document. Pure.</summary>
    public static IReadOnlyList<string> Compare(
        IEnumerable<ConsentRequirement> requirements,
        IEnumerable<LatestConsent> local,
        IEnumerable<RemoteDocumentConsent> remote)
    {
        var localByDocument = local.ToDictionary(l => l.Document, StringComparer.Ordinal);
        var remoteByDocument = remote.GroupBy(r => r.Document, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var differences = new List<string>();
        foreach (var requirement in requirements)
        {
            localByDocument.TryGetValue(requirement.Document, out var mine);
            remoteByDocument.TryGetValue(requirement.Document, out var theirs);
            if (mine is null)
            {
                if (theirs?.LastAction is { } action) differences.Add($"{requirement.Document}: only in the service ({action})");
                continue;
            }
            if (theirs?.LastAction is null)
            {
                differences.Add($"{requirement.Document}: missing in the service (local {mine.Action} v{mine.Version})");
                continue;
            }
            if (!string.Equals(mine.Action, theirs.LastAction, StringComparison.Ordinal))
            {
                differences.Add($"{requirement.Document}: action local {mine.Action}, service {theirs.LastAction}");
            }
            else if (mine.Action == ConsentActions.Granted && theirs.GrantedVersion != mine.Version)
            {
                differences.Add($"{requirement.Document}: version local v{mine.Version}, service v{theirs.GrantedVersion?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
            }
        }
        return differences;
    }

    private sealed class StatusDto
    {
        public List<DocumentDto>? Documents { get; set; }
    }

    private sealed class DocumentDto
    {
        public string? Document { get; set; }
        public string? LastAction { get; set; }
        public int? GrantedVersion { get; set; }
    }
}
