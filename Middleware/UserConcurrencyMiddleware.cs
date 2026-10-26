using System.Collections.Concurrent;
using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Middleware;

/// <summary>
/// Limits the number of concurrent API operations per authenticated user.
/// Default: BillingOptions.ConcurrencyDefaultLimit (3). Configurable per user via User.MaxConcurrentOperations.
/// When limit is exceeded, returns 429 Too Many Requests.
///
/// Also computes the concurrency tier atomically at semaphore acquisition and stores it in
/// HttpContext.Items[TierItemKey], so the billing debit in ApiController sees a deterministic
/// slot index — not a value that drifts due to Release races while the request processes.
///
/// Order of acquisition (#69): the user's slot first — including the wait in the user's queue —
/// and only then the global slot, after a short wait (<c>GlobalConcurrencyWaitMilliseconds</c>). A request queued behind its own user's
/// limit therefore never holds a global slot, so one user's backlog cannot starve other users.
/// Every slot taken is released in a <c>finally</c> on every path: success, 429, 503, an
/// exception from the pipeline and a request cancelled by the client.
/// </summary>
public class UserConcurrencyMiddleware
{
    public const string TierItemKey = "ConcurrencyTier";

    /// <summary>
    /// Per-user counting gate with a limit that can change while requests run. Unlike a
    /// replaced <see cref="SemaphoreSlim"/>, the count of running requests survives a new limit.
    /// </summary>
    internal sealed class UserGate
    {
        private sealed class Waiter
        {
            public readonly TaskCompletionSource<int> Granted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Done;
        }

        private readonly object _lock = new();
        private readonly LinkedList<Waiter> _waiters = new();
        private int _limit;
        private int _held;

        public UserGate(int limit) => _limit = Math.Max(0, limit);

        public int Held { get { lock (_lock) return _held; } }

        public int Limit { get { lock (_lock) return _limit; } }

        /// <summary>Applies a new limit; a lower one takes effect as running requests finish.</summary>
        public void SetLimit(int limit)
        {
            lock (_lock)
            {
                _limit = Math.Max(0, limit);
                GrantWaiters();
            }
        }

        /// <summary>Returns the 1-based slot taken, or 0 on timeout. Throws when cancelled.</summary>
        public async Task<int> WaitAsync(TimeSpan timeout, CancellationToken ct)
        {
            Waiter waiter;
            LinkedListNode<Waiter> node;
            lock (_lock)
            {
                if (_waiters.Count == 0 && _held < _limit)
                {
                    return ++_held;
                }
                if (timeout <= TimeSpan.Zero)
                {
                    return 0;
                }
                waiter = new Waiter();
                node = _waiters.AddLast(waiter);
            }

            try
            {
                return await waiter.Granted.Task.WaitAsync(timeout, ct);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                lock (_lock)
                {
                    if (waiter.Done)
                    {
                        // The slot was granted in the race with the timeout: give it back.
                        _held--;
                        GrantWaiters();
                    }
                    else
                    {
                        _waiters.Remove(node);
                    }
                }
                if (ex is TimeoutException) return 0;
                throw;
            }
        }

        public void Release()
        {
            lock (_lock)
            {
                _held--;
                GrantWaiters();
            }
        }

        private void GrantWaiters()
        {
            while (_waiters.First != null && _held < _limit)
            {
                var waiter = _waiters.First.Value;
                _waiters.RemoveFirst();
                waiter.Done = true;
                waiter.Granted.SetResult(++_held);
            }
        }
    }

    private static readonly ConcurrentDictionary<Guid, UserGate> Semaphores = new();

    private const int LimitCacheDurationSeconds = 60;
    private static readonly ConcurrentDictionary<Guid, (int Limit, int QueueTimeoutSeconds, DateTimeOffset ExpiresAt)> LimitCache = new();

    /// <summary>
    /// Removes the cached concurrency settings for the given user, forcing a DB re-read on the
    /// next request. Call after admin updates to MaxConcurrentOperations or ConcurrencyQueueTimeoutSeconds.
    /// </summary>
    public static void InvalidateLimitCache(Guid userId) => LimitCache.TryRemove(userId, out _);

    /// <summary>
    /// Returns the number of in-flight requests for the given user. Kept for diagnostics.
    /// Billing should read the tier from HttpContext.Items[TierItemKey] instead — it is atomic
    /// with semaphore acquisition, unlike this live counter which drifts during request processing.
    /// </summary>
    public static int GetInFlightCount(Guid userId)
    {
        return Semaphores.TryGetValue(userId, out var gate) ? gate.Held : 0;
    }

    /// <summary>
    /// Computes the tier multiplier for a given 1-based slot (1 = first in, N = Nth in).
    /// Exposed so tests can verify the same formula used by the middleware.
    /// </summary>
    public static int ComputeTier(int slot, int tierSize, int maxTier)
    {
        tierSize = Math.Max(1, tierSize);
        maxTier = Math.Max(1, maxTier);
        var slotIndex = Math.Max(0, slot - 1);
        return Math.Max(1, Math.Min(maxTier, (slotIndex / tierSize) + 1));
    }

    private readonly RequestDelegate _next;
    private readonly ILogger<UserConcurrencyMiddleware> _logger;
    private readonly BillingOptions _billingOptions;
    // One middleware instance serves the whole pipeline, so the global cap lives on the instance.
    // It used to be static and was swapped in the constructor; a request releasing into a
    // semaphore it never took from could then overfill it.
    private readonly SemaphoreSlim? _globalSemaphore;
    private readonly int _globalCapacity;
    private readonly TimeSpan _globalWait;

    /// <summary>
    /// Free global slots, or null when the global cap is off. Diagnostics and tests: after any
    /// mix of finished, rejected, failed and cancelled requests it must be back at capacity.
    /// </summary>
    public int? GlobalSlotsAvailable => _globalSemaphore?.CurrentCount;

    public UserConcurrencyMiddleware(
        RequestDelegate next,
        ILogger<UserConcurrencyMiddleware> logger,
        IOptions<BillingOptions> billingOptions)
    {
        _next = next;
        _logger = logger;
        _billingOptions = billingOptions.Value;

        var globalLimit = BillingOptions.EffectiveGlobalLimit(_billingOptions, Environment.ProcessorCount);
        _globalWait = TimeSpan.FromMilliseconds(Math.Max(0, _billingOptions.GlobalConcurrencyWaitMilliseconds));
        if (globalLimit > 0)
        {
            _globalSemaphore = new SemaphoreSlim(globalLimit, globalLimit);
            _globalCapacity = globalLimit;
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.HasValue ||
            !context.Request.Path.Value!.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var userId = ResolveUserId(context);
        if (userId == null)
        {
            await _next(context);
            return;
        }

        var (limit, queueTimeoutSeconds) = await GetUserSettings(userId.Value, context.RequestServices);

        // The gate is kept when the limit changes, so running requests stay counted.
        var gate = Semaphores.GetOrAdd(userId.Value, _ => new UserGate(limit));
        gate.SetLimit(limit);

        var waitTimeout = queueTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(queueTimeoutSeconds)
            : TimeSpan.Zero;

        // The finally releases exactly what was taken.
        var globalSemaphore = _globalSemaphore;
        var userSlotAcquired = false;
        var globalSlotAcquired = false;

        try
        {
            // 1) The user's own slot, waiting in the user's queue. No global slot is held here.
            int slot;
            try
            {
                slot = await gate.WaitAsync(waitTimeout, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The client went away while queued; nobody is left to answer.
                return;
            }
            userSlotAcquired = slot > 0;

            if (!userSlotAcquired)
            {
                _logger.LogWarning("User {UserId} exceeded concurrency limit ({Limit}), queue timeout {QueueTimeout}s",
                    userId.Value, limit, queueTimeoutSeconds);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers["Retry-After"] = queueTimeoutSeconds > 0 ? "1" : "5";
                var factory = context.RequestServices.GetRequiredService<IStringLocalizerFactory>();
                var localizer = factory.Create(typeof(SharedStrings));
                await context.Response.WriteAsJsonAsync(new
                {
                    message = localizer["TooManyRequests"].Value,
                    limit,
                    retryAfter = queueTimeoutSeconds > 0 ? 1 : 5,
                });
                return;
            }

            // 2) The global slot after a short wait, so the time spent in the user's queue is not wasted.
            if (globalSemaphore != null)
            {
                try
                {
                    globalSlotAcquired = await globalSemaphore.WaitAsync(_globalWait, context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    return;
                }
                if (!globalSlotAcquired)
                {
                    _logger.LogWarning("Global concurrency limit ({Limit}) reached", _globalCapacity);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers["Retry-After"] = "10";
                    var factory = context.RequestServices.GetRequiredService<IStringLocalizerFactory>();
                    var localizer = factory.Create(typeof(SharedStrings));
                    await context.Response.WriteAsJsonAsync(new
                    {
                        message = localizer["ServerAtCapacity"].Value,
                        retryAfter = 10,
                    });
                    return;
                }
            }

            // Atomic: the slot was fixed when the gate granted it, not at debit time.
            var tier = ComputeTier(slot, _billingOptions.ConcurrencyTierSize, _billingOptions.MaxConcurrencyTier);
            context.Items[TierItemKey] = tier;

            await _next(context);
        }
        finally
        {
            if (globalSlotAcquired) globalSemaphore!.Release();
            if (userSlotAcquired) gate.Release();
        }
    }

    private static Guid? ResolveUserId(HttpContext context)
    {
        var claim = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                    ?? context.User?.FindFirst("sub")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static async Task<(int Limit, int QueueTimeoutSeconds)> GetUserSettings(Guid userId, IServiceProvider services)
    {
        if (LimitCache.TryGetValue(userId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return (cached.Limit, cached.QueueTimeoutSeconds);
        }

        var billing = services.GetService<IOptions<BillingOptions>>()?.Value;
        var defaultLimit = billing?.ConcurrencyDefaultLimit ?? 3;
        var defaultQueueTimeout = billing?.ConcurrencyQueueTimeoutSeconds ?? 0;

        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.MaxConcurrentOperations, u.ConcurrencyQueueTimeoutSeconds })
                .FirstOrDefaultAsync();

            var limit = row?.MaxConcurrentOperations ?? defaultLimit;
            var queueTimeout = row?.ConcurrencyQueueTimeoutSeconds ?? defaultQueueTimeout;
            LimitCache[userId] = (limit, queueTimeout, DateTimeOffset.UtcNow.AddSeconds(LimitCacheDurationSeconds));
            return (limit, queueTimeout);
        }
        catch
        {
            return (defaultLimit, defaultQueueTimeout);
        }
    }
}
