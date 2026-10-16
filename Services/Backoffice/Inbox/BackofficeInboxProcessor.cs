using System.Text.Json;
using System.Text.RegularExpressions;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Backoffice.Inbox;

/// <summary>
/// Processes the backoffice inbox in the background: woken by <see cref="BackofficeInboxSignal"/>
/// right after the webhook endpoint or polling stored something, otherwise every
/// <see cref="PollInterval"/>. Each unprocessed, due item goes to its type's handler; an
/// unknown type is marked processed. A failing handler is retried from 30 s up to every
/// hour; after <see cref="MaxAttempts"/> the item is dead (<see cref="BackofficeWebhookInboxItem.DeadAt"/>),
/// logged as an error starting with <see cref="AlertPrefix"/>, kept by the cleanup and
/// retried only when an admin replays it (<see cref="ReplayDeadAsync"/>).
///
/// Several instances may run against one database: due items are claimed by moving their
/// <c>NextAttemptAt</c> <see cref="ClaimDuration"/> ahead, on PostgreSQL in one statement with
/// <c>FOR UPDATE SKIP LOCKED</c>, so each item runs on one instance (#79). A batch stops after
/// <see cref="BatchBudget"/> and gives back what it did not reach.
///
/// Registered only when a backoffice module is Shadow or On (never on a PrivateServer).
/// </summary>
public sealed class BackofficeInboxProcessor : BackgroundService
{
    public const int BatchSize = 50;
    public const int MaxAttempts = 20;
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxRetry = TimeSpan.FromHours(1);

    /// <summary>A claimed item is not due for others for this long; a batch gives up well before.</summary>
    public static readonly TimeSpan ClaimDuration = TimeSpan.FromMinutes(10);

    /// <summary>A batch stops starting items after this and releases the rest.</summary>
    public static readonly TimeSpan BatchBudget = TimeSpan.FromMinutes(5);

    /// <summary>Start of the error logged for a dead item (and daily while dead items exist): the alert marker.</summary>
    public const string AlertPrefix = "Backoffice inbox alert:";

    /// <summary>
    /// Processed items are kept this long so a late redelivery or a re-polled event is still
    /// recognised (the service keeps events 30 days).
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(45);
    public static readonly TimeSpan CleanupInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan ErrorPause = TimeSpan.FromSeconds(30);
    private const int MaxErrorLength = 512;

    private readonly IServiceScopeFactory _scopes;
    private readonly BackofficeInboxSignal _signal;
    private readonly TimeProvider _time;
    private readonly ILogger<BackofficeInboxProcessor> _logger;
    private DateTimeOffset _nextCleanup;

    public BackofficeInboxProcessor(
        IServiceScopeFactory scopes,
        BackofficeInboxSignal signal,
        TimeProvider time,
        ILogger<BackofficeInboxProcessor> logger)
    {
        _scopes = scopes;
        _signal = signal;
        _time = time;
        _logger = logger;
    }

    /// <summary>Delay before attempt <paramref name="failedAttempts"/> + 1: 30 s, 1 min, 2 min, … capped at 1 h.</summary>
    public static TimeSpan RetryDelay(int failedAttempts)
    {
        var exponent = Math.Clamp(failedAttempts - 1, 0, 16);
        var delay = TimeSpan.FromTicks(FirstRetry.Ticks * (1L << exponent));
        return delay > MaxRetry ? MaxRetry : delay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never hold up application start
        _nextCleanup = _time.GetUtcNow() + TimeSpan.FromMinutes(5);

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = PollInterval;
            try
            {
                // A full batch means more may be due right now.
                while (await ProcessDueAsync(stoppingToken) >= BatchSize)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                }

                var now = _time.GetUtcNow();
                if (now >= _nextCleanup)
                {
                    _nextCleanup = now + CleanupInterval;
                    await CleanupAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backoffice inbox pass failed");
                wait = ErrorPause;
            }

            try
            {
                await _signal.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Claims and runs up to <see cref="BatchSize"/> due items, oldest first. Returns how many were claimed.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken)
    {
        List<Guid> ids;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ids = await ClaimDueAsync(db, _time.GetUtcNow(), cancellationToken);
        }

        var started = _time.GetTimestamp();
        var done = 0;
        try
        {
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_time.GetElapsedTime(started) > BatchBudget) break;
                await ProcessOneAsync(id, cancellationToken);
                done++;
            }
        }
        finally
        {
            if (done < ids.Count)
            {
                // Not cancellable: what this instance will not run must not wait for the claim to expire.
                await ReleaseAsync(ids.Skip(done).ToList());
            }
        }
        return ids.Count;
    }

    /// <summary>
    /// Due items (unprocessed, not dead, <c>NextAttemptAt</c> passed), oldest first, made not due
    /// for <see cref="ClaimDuration"/>. On PostgreSQL one <c>UPDATE … FOR UPDATE SKIP LOCKED</c>,
    /// so two instances never claim the same item; elsewhere (InMemory) read-then-write.
    /// </summary>
    internal static async Task<List<Guid>> ClaimDueAsync(ApplicationDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var until = now + ClaimDuration;
        if (db.Database.IsNpgsql())
        {
            var t = SqlNames.Of<BackofficeWebhookInboxItem>(db);
            var next = t[nameof(BackofficeWebhookInboxItem.NextAttemptAt)];
            var id = t[nameof(BackofficeWebhookInboxItem.Id)];
            var sql = $$"""
                UPDATE {{t.Table}} SET {{next}} = {1}
                WHERE {{id}} IN (
                    SELECT {{id}} FROM {{t.Table}}
                    WHERE {{t[nameof(BackofficeWebhookInboxItem.ProcessedAt)]}} IS NULL
                      AND {{t[nameof(BackofficeWebhookInboxItem.DeadAt)]}} IS NULL
                      AND {{next}} <= {0}
                    ORDER BY {{t[nameof(BackofficeWebhookInboxItem.ReceivedAt)]}}
                    LIMIT {{BatchSize}}
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """;
            var claimed = await db.BackofficeWebhookInboxItems
                .FromSqlRaw(sql, now.ToUniversalTime(), until.ToUniversalTime())
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            return claimed.OrderBy(i => i.ReceivedAt).Select(i => i.Id).ToList();
        }

        var due = await db.BackofficeWebhookInboxItems
            .Where(i => i.ProcessedAt == null && i.DeadAt == null && i.NextAttemptAt != null && i.NextAttemptAt <= now)
            .OrderBy(i => i.ReceivedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        foreach (var item in due) item.NextAttemptAt = until;
        await db.SaveChangesAsync(cancellationToken);
        return due.Select(i => i.Id).ToList();
    }

    /// <summary>Makes claimed items this instance did not run due again at once.</summary>
    private async Task ReleaseAsync(IReadOnlyList<Guid> ids)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = _time.GetUtcNow();
            var rows = await db.BackofficeWebhookInboxItems
                .Where(i => ids.Contains(i.Id) && i.ProcessedAt == null && i.DeadAt == null)
                .ToListAsync(CancellationToken.None);
            foreach (var row in rows) row.NextAttemptAt = now;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The claim expires on its own after ClaimDuration.
            _logger.LogWarning(ex, "Backoffice inbox: could not release {Count} claimed event(s)", ids.Count);
        }
    }

    /// <summary>One item in its own scope, so a failing handler cannot leave state behind for the next.</summary>
    private async Task ProcessOneAsync(Guid id, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var item = await db.BackofficeWebhookInboxItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item == null || item.ProcessedAt != null || item.DeadAt != null) return; // done meanwhile (another instance)

        item.Attempts++;
        var handler = scope.ServiceProvider.GetRequiredService<EventHandlerRegistry>().Find(item.Type);
        if (handler == null)
        {
            _logger.LogDebug("Backoffice event {EventType} {EventId} is not handled; marked processed", item.Type, item.WebhookId);
            MarkProcessed(item);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        try
        {
            JsonElement data;
            using (var payload = JsonDocument.Parse(item.PayloadJson))
            {
                data = payload.RootElement.Clone(); // handlers may keep it
            }
            await handler.HandleAsync(
                new BackofficeEvent(item.WebhookId, item.Type, data, item.Source, item.ReceivedAt, item.Attempts),
                cancellationToken);
            MarkProcessed(item);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Nothing a failed handler left in the context is saved; only the outcome is.
            db.ChangeTracker.Clear();
            db.Attach(item);
            db.Entry(item).State = EntityState.Modified;
            item.Error = ErrorText(ex);
            if (item.Attempts >= MaxAttempts)
            {
                item.NextAttemptAt = null;
                item.DeadAt = _time.GetUtcNow();
                _logger.LogError(ex,
                    AlertPrefix + " event {EventType} {EventId} is dead after {Attempts} failed attempts{Consequence}; "
                    + "fix the cause and replay it in /Admin",
                    item.Type, item.WebhookId, item.Attempts, Consequence(item.Type));
            }
            else
            {
                item.NextAttemptAt = _time.GetUtcNow() + RetryDelay(item.Attempts);
                _logger.LogWarning(ex, "Backoffice event {EventType} {EventId} failed (attempt {Attempts}); retry at {NextAttemptAt}",
                    item.Type, item.WebhookId, item.Attempts, item.NextAttemptAt);
            }
        }

        // On success the handler's own changes, if any, are saved together with the outcome.
        await db.SaveChangesAsync(cancellationToken);
    }

    private void MarkProcessed(BackofficeWebhookInboxItem item)
    {
        item.ProcessedAt = _time.GetUtcNow();
        item.NextAttemptAt = null;
        item.Error = null;
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var deleted = await DeleteProcessedAsync(db, _time.GetUtcNow(), cancellationToken);
        if (deleted > 0)
        {
            _logger.LogInformation("Backoffice inbox cleanup deleted {Count} processed event(s)", deleted);
        }

        // Dead items stay until replayed; repeat the alert daily so they are not forgotten.
        var dead = await CountDeadAsync(db, cancellationToken);
        if (dead > 0)
        {
            _logger.LogError(AlertPrefix + " {Count} dead event(s) wait for a replay in /Admin", dead);
        }
    }

    /// <summary>What a dead event of <paramref name="type"/> leaves undone, for the alert.</summary>
    internal static string Consequence(string type) => type == BackofficeEventTypes.BillingCheckoutCompleted
        ? " (a paid purchase whose credits may not be granted; replaying grants them once)"
        : "";

    /// <summary>Items that ran out of attempts and wait for a replay.</summary>
    public static Task<int> CountDeadAsync(ApplicationDbContext db, CancellationToken cancellationToken = default) =>
        db.BackofficeWebhookInboxItems.CountAsync(i => i.DeadAt != null && i.ProcessedAt == null, cancellationToken);

    /// <summary>
    /// Puts dead items (all, or the one with <paramref name="webhookId"/>) back in the queue with a
    /// fresh round of attempts, due now. Handlers are idempotent (credits are granted once per
    /// payment), so a replay of an event that partly ran is safe. Returns the replayed ids.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ReplayDeadAsync(
        ApplicationDbContext db, DateTimeOffset now, string? webhookId = null, CancellationToken cancellationToken = default)
    {
        var query = db.BackofficeWebhookInboxItems.Where(i => i.DeadAt != null && i.ProcessedAt == null);
        if (webhookId != null) query = query.Where(i => i.WebhookId == webhookId);
        var dead = await query.ToListAsync(cancellationToken);
        foreach (var item in dead)
        {
            item.DeadAt = null;
            item.Attempts = 0;
            item.NextAttemptAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        return dead.Select(i => i.Id).ToList();
    }

    /// <summary>Deletes items processed more than <see cref="Retention"/> ago.</summary>
    public static async Task<int> DeleteProcessedAsync(ApplicationDbContext db, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var cutoff = now - Retention;
        var old = db.BackofficeWebhookInboxItems.Where(i => i.ProcessedAt != null && i.ProcessedAt < cutoff);
        if (db.Database.IsRelational()) return await old.ExecuteDeleteAsync(cancellationToken);
        var rows = await old.ToListAsync(cancellationToken);
        db.BackofficeWebhookInboxItems.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    private static readonly Regex EmailPattern = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)+",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// What is stored in <see cref="BackofficeWebhookInboxItem.Error"/>: the exception type and
    /// message with e-mail addresses masked (a safety net for handlers that break the
    /// no-payload-in-exceptions rule of <see cref="IBackofficeEventHandler"/>), at most 512 chars.
    /// </summary>
    internal static string ErrorText(Exception ex)
    {
        string message;
        try
        {
            message = EmailPattern.Replace(ex.Message, "[email]");
        }
        catch (RegexMatchTimeoutException)
        {
            message = "(message withheld)";
        }
        return Truncate($"{ex.GetType().Name}: {message}");
    }

    private static string Truncate(string value) => value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}
