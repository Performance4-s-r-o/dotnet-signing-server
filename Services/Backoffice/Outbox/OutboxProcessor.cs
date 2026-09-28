using System.Diagnostics;
using System.Security.Cryptography;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <summary>Where an item stands after <see cref="OutboxProcessor.TryDispatchNowAsync"/>.</summary>
/// <param name="Status">A <see cref="BackofficeOutboxStatus"/> value; null when the item does not exist (not committed).</param>
/// <param name="Attempted">Whether this call made an attempt itself.</param>
public sealed record OutboxDispatchResult(string? Status, bool Attempted, string? RemoteId, string? LastError)
{
    public bool IsSent => Status == BackofficeOutboxStatus.Sent;
}

/// <summary>
/// Sends outbox items: claim, decrypt, hand to the kind's <see cref="IOutboxHandler"/>,
/// classify with <see cref="RetryPolicy"/>, store the outcome. Shared by the background
/// <see cref="BackofficeOutboxDispatcher"/> and <see cref="TryDispatchNowAsync"/>.
/// Never runs on a request path unless a caller explicitly asks for an immediate attempt.
/// </summary>
public sealed class OutboxProcessor
{
    public const string HttpClientName = "P4Backoffice.Outbox";

    /// <summary>Per-attempt limit; also the named client's timeout.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A batch is abandoned (remaining items released) after this, well inside <see cref="OutboxClaim.LockDuration"/>.</summary>
    public static readonly TimeSpan BatchBudget = TimeSpan.FromSeconds(90);

    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _httpClients;
    private readonly IReadOnlyDictionary<string, IOutboxHandler> _handlers;
    private readonly OutboxPayloadProtector _protector;
    private readonly OutboxCircuitBreaker _breaker;
    private readonly TimeProvider _time;
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly OutboxKindFilter _kinds;

    public OutboxProcessor(
        IServiceScopeFactory scopes,
        IHttpClientFactory httpClients,
        IEnumerable<IOutboxHandler> handlers,
        OutboxPayloadProtector protector,
        OutboxCircuitBreaker breaker,
        TimeProvider time,
        ILogger<OutboxProcessor> logger,
        OutboxKindFilter? kinds = null)
    {
        _kinds = kinds ?? OutboxKindFilter.None;
        _scopes = scopes;
        _httpClients = httpClients;
        _handlers = handlers.GroupBy(h => h.Kind, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        _protector = protector;
        _breaker = breaker;
        _time = time;
        _logger = logger;
    }

    /// <summary>Claims one batch of due items and attempts each. Returns how many were claimed.</summary>
    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var items = await OutboxClaim.ClaimDueAsync(db, _time.GetUtcNow(), cancellationToken, _kinds.PausedKindPrefixes);
        if (items.Count == 0) return 0;

        var started = Stopwatch.StartNew();
        try
        {
            foreach (var item in items)
            {
                if (started.Elapsed > BatchBudget)
                {
                    item.LockedUntil = null; // let the next pass (or another instance) take it
                    continue;
                }
                await AttemptAsync(item, AttemptTimeout, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: release what was not finished so another instance need not wait for the lease.
            foreach (var item in items.Where(i => i.LockedUntil != null)) item.LockedUntil = null;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        return items.Count;
    }

    /// <summary>
    /// Attempts one item right away for callers that want the result (support form, …),
    /// waiting at most <paramref name="timeout"/>. If the dispatcher holds the item, waits for
    /// its attempt instead of sending twice. The item stays in the outbox either way; a failed
    /// attempt is retried by the dispatcher on the usual schedule.
    /// </summary>
    public async Task<OutboxDispatchResult> TryDispatchNowAsync(Guid id, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var item = await OutboxClaim.ClaimOneAsync(db, id, _time.GetUtcNow(), cancellationToken);
            if (item != null)
            {
                var left = timeout - started.Elapsed;
                var attemptTimeout = left < AttemptTimeout ? left : AttemptTimeout;
                if (attemptTimeout <= TimeSpan.Zero) attemptTimeout = TimeSpan.FromMilliseconds(1);
                await AttemptAsync(item, attemptTimeout, cancellationToken);
                await db.SaveChangesAsync(CancellationToken.None);
                return new OutboxDispatchResult(item.Status, true, item.RemoteId, item.LastError);
            }
        }

        // Not claimable: missing, already finished, waiting for its next attempt, or held by
        // someone else. Only the last case is worth waiting for.
        while (true)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.BackofficeOutboxItems.AsNoTracking()
                .Where(i => i.Id == id)
                .Select(i => new { i.Status, i.LockedUntil, i.RemoteId, i.LastError })
                .FirstOrDefaultAsync(cancellationToken);
            if (row == null) return new OutboxDispatchResult(null, false, null, null);

            var held = row.Status == BackofficeOutboxStatus.Pending && row.LockedUntil > _time.GetUtcNow();
            if (!held || started.Elapsed >= timeout)
                return new OutboxDispatchResult(row.Status, false, row.RemoteId, row.LastError);

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
    }

    /// <summary>One attempt on a claimed, tracked item; records the outcome on it (not saved).</summary>
    private async Task AttemptAsync(BackofficeOutboxItem item, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await SendAsync(item, timeout, cancellationToken);
        var outcome = RetryPolicy.Apply(item, result, _time.GetUtcNow());
        _breaker.Record(result);

        switch (outcome)
        {
            case OutboxOutcome.Sent:
                _logger.LogInformation(
                    "Backoffice outbox {ItemId} ({Kind}) sent after {Attempts} attempt(s)", item.Id, item.Kind, item.Attempts);
                break;
            case OutboxOutcome.Retry:
                _logger.LogWarning(
                    "Backoffice outbox {ItemId} ({Kind}) attempt {Attempts} failed: {Error}; next attempt at {NextAttemptAt:o}",
                    item.Id, item.Kind, item.Attempts, item.LastError, item.NextAttemptAt);
                break;
            case OutboxOutcome.Blocked:
                _logger.LogError(
                    "Backoffice outbox {ItemId} ({Kind}) blocked: {Error}. Check P4Backoffice:SecretKey and its scopes, then requeue blocked items in /Admin",
                    item.Id, item.Kind, item.LastError);
                break;
            case OutboxOutcome.Suppressed:
                _logger.LogInformation(
                    "Backoffice outbox {ItemId} ({Kind}) not sent: recipient is suppressed", item.Id, item.Kind);
                break;
            case OutboxOutcome.Dead:
                _logger.LogError(
                    "Backoffice outbox {ItemId} ({Kind}, {SubjectRef}) is dead: {Error}",
                    item.Id, item.Kind, item.SubjectRef, item.LastError);
                break;
        }
    }

    private async Task<OutboxAttemptResult> SendAsync(BackofficeOutboxItem item, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(item.Kind, out var handler))
        {
            return OutboxAttemptResult.LocalFailure($"No handler for kind '{item.Kind}'");
        }
        if (item.PayloadProtected == null)
        {
            return OutboxAttemptResult.LocalFailure("Payload missing");
        }

        string payload;
        try
        {
            payload = _protector.Unprotect(item.PayloadProtected);
        }
        catch (CryptographicException)
        {
            // The key ring that encrypted it is gone; retrying will not bring it back.
            return OutboxAttemptResult.Fatal("Payload cannot be decrypted (Data Protection key ring changed)");
        }

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(timeout);
        var request = new OutboxRequest(
            _httpClients.CreateClient(HttpClientName), item.Id, item.Kind, payload, item.Attempts + 1, item.Critical, item.SubjectRef);
        try
        {
            return await handler.SendAsync(request, attemptCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OutboxAttemptResult.Timeout();
        }
        catch (HttpRequestException ex)
        {
            return OutboxAttemptResult.NetworkError(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Backoffice outbox handler for {Kind} failed on {ItemId}", item.Kind, item.Id);
            return OutboxAttemptResult.LocalFailure($"Handler error: {ex.GetType().Name}");
        }
    }
}
