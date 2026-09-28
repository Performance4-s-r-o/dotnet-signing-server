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
/// hour and given up after <see cref="MaxAttempts"/> (logged as an error).
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

    /// <summary>Runs the handlers of up to <see cref="BatchSize"/> due items, oldest first. Returns how many were attempted.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        List<Guid> ids;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ids = await db.BackofficeWebhookInboxItems.AsNoTracking()
                .Where(i => i.ProcessedAt == null && i.NextAttemptAt != null && i.NextAttemptAt <= now)
                .OrderBy(i => i.ReceivedAt)
                .Take(BatchSize)
                .Select(i => i.Id)
                .ToListAsync(cancellationToken);
        }

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessOneAsync(id, cancellationToken);
        }
        return ids.Count;
    }

    /// <summary>One item in its own scope, so a failing handler cannot leave state behind for the next.</summary>
    private async Task ProcessOneAsync(Guid id, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var item = await db.BackofficeWebhookInboxItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item == null || item.ProcessedAt != null) return; // done meanwhile (another instance)

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
                _logger.LogError(ex, "Backoffice event {EventType} {EventId} failed {Attempts} times; giving up",
                    item.Type, item.WebhookId, item.Attempts);
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
