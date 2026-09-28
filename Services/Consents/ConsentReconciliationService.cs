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
/// Users whose consent batch is still in the outbox are skipped. On PostgreSQL one instance
/// runs it at a time (session advisory lock); the last run is kept in <c>BackofficeStates</c>.
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

    /// <summary>"P4BOCREC" as ASCII.</summary>
    public const long AdvisoryLockKey = 0x5034_424F_4352_4543;

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
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (!db.Database.IsNpgsql())
        {
            return await RunLockedAsync(db, cancellationToken);
        }

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({AdvisoryLockKey}) AS \"Value\"")
                .SingleAsync(cancellationToken);
            if (!locked) return null;
            try
            {
                return await RunLockedAsync(db, cancellationToken);
            }
            finally
            {
                await db.Database.SqlQuery<bool>($"SELECT pg_advisory_unlock({AdvisoryLockKey}) AS \"Value\"")
                    .SingleAsync(CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
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

    /// <summary>One pass over the recent subjects; returns how many differ. Throws when the service fails.</summary>
    internal async Task<int> ReconcileAsync(ApplicationDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = now - Lookback;
        var userIds = await db.ConsentRecords.AsNoTracking()
            .Where(c => c.OccurredAt >= since && c.OutboxItemId != null)
            .Select(c => c.UserId)
            .Distinct()
            .Take(MaxSubjectsPerRun)
            .ToListAsync(cancellationToken);

        var requirements = ConsentRequirements.From(_options.Value.Consents);
        var http = _httpClients.CreateClient(HttpClientName);
        var differing = 0;
        foreach (var userId in userIds)
        {
            var subjectRef = ConsentRequirements.SubjectRef(userId);
            var pending = await db.BackofficeOutboxItems.AsNoTracking()
                .AnyAsync(i => i.Kind == ConsentRequirements.OutboxKind
                               && i.SubjectRef == subjectRef
                               && i.Status != BackofficeOutboxStatus.Sent, cancellationToken);
            if (pending) continue;

            var local = await ConsentStatusProvider.LatestAsync(db, userId, cancellationToken);
            var remote = await FetchAsync(http, subjectRef, cancellationToken);
            var differences = Compare(requirements, local, remote);
            if (differences.Count > 0)
            {
                differing++;
                _logger.LogWarning("[consents] {SubjectRef} differs from the service: {Differences}",
                    subjectRef, string.Join("; ", differences));
            }
        }

        _logger.LogInformation("[consents] reconciliation: {Subjects} subjects checked, {Differing} differ", userIds.Count, differing);
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
