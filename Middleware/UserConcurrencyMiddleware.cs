using System.Collections.Concurrent;
using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
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
///
/// The user's queue is bounded (#69): at most <c>UserQueueMaxSeconds</c> of waiting and
/// <c>UserQueueLengthFactor</c> × limit waiting requests, then 429 with an honest Retry-After.
/// The trusted caller is exempt from both bounds; it shares its capacity between tenants itself.
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

        public int Waiting { get { lock (_lock) return _waiters.Count; } }

        /// <summary>Applies a new limit; a lower one takes effect as running requests finish.</summary>
        public void SetLimit(int limit)
        {
            lock (_lock)
            {
                _limit = Math.Max(0, limit);
                GrantWaiters();
            }
        }

        /// <summary>Slot result: the queue already holds as many as it may (#69).</summary>
        public const int QueueFull = -1;

        /// <summary>Returns the 1-based slot taken, or 0 on timeout. Throws when cancelled.</summary>
        public Task<int> WaitAsync(TimeSpan timeout, CancellationToken ct) => WaitAsync(timeout, int.MaxValue, ct);

        /// <summary>
        /// Returns the 1-based slot taken, 0 on timeout, or <see cref="QueueFull"/> when
        /// <paramref name="maxWaiting"/> requests are already queued. Throws when cancelled.
        /// </summary>
        public async Task<int> WaitAsync(TimeSpan timeout, int maxWaiting, CancellationToken ct)
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
                if (_waiters.Count >= maxWaiting)
                {
                    return QueueFull;
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

    private static long _rejectedQueueFull;
    private static long _rejectedTimeout;
    private static long _rejectedNoQueue;

    /// <summary>All users' queues together, for the diagnostics endpoint (#69). No user ids.</summary>
    public static UserQueuesSnapshot QueuesSnapshot()
    {
        int users = 0, running = 0, waiting = 0;
        foreach (var gate in Semaphores.Values)
        {
            var held = gate.Held;
            var queued = gate.Waiting;
            if (held == 0 && queued == 0) continue;
            users++;
            running += held;
            waiting += queued;
        }
        return new UserQueuesSnapshot(users, running, waiting,
            Interlocked.Read(ref _rejectedNoQueue),
            Interlocked.Read(ref _rejectedTimeout),
            Interlocked.Read(ref _rejectedQueueFull));
    }

    /// <summary>Users with work running or queued, and the 429s since start by cause.</summary>
    public sealed record UserQueuesSnapshot(
        int ActiveUsers,
        int Running,
        int Waiting,
        long RejectedWithoutQueue,
        long RejectedAfterTimeout,
        long RejectedQueueFull);

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
    private readonly CapacityPool _global;
    private readonly TimeSpan _globalWait;

    /// <summary>
    /// Free global slots, or null when the global cap is off. Diagnostics and tests: after any
    /// mix of finished, rejected, failed and cancelled requests it must be back at capacity.
    /// </summary>
    public int? GlobalSlotsAvailable => _global.Available;

    /// <summary>
    /// Tests only: a private set of pools built from <paramref name="billingOptions"/>. Internal, so
    /// the pipeline can only ever use the constructor that takes the shared pools.
    /// </summary>
    internal UserConcurrencyMiddleware(
        RequestDelegate next,
        ILogger<UserConcurrencyMiddleware> logger,
        IOptions<BillingOptions> billingOptions)
        : this(next, logger, billingOptions, new CapacityPools(billingOptions.Value, Environment.ProcessorCount))
    {
    }

    /// <summary>The pipeline's constructor: the shared pools, so diagnostics see the same global pool.</summary>
    [ActivatorUtilitiesConstructor]
    public UserConcurrencyMiddleware(
        RequestDelegate next,
        ILogger<UserConcurrencyMiddleware> logger,
        IOptions<BillingOptions> billingOptions,
        CapacityPools pools)
    {
        _next = next;
        _logger = logger;
        _billingOptions = billingOptions.Value;
        _global = pools.Global;
        _globalWait = pools.Wait;
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

        // #69: the queue is bounded in time and length, except for the trusted caller, which
        // queues its own tenants fairly and keeps what its account has stored.
        var trusted = PortalTrust.IsTrustedCaller(context);
        var queueSeconds = BillingOptions.EffectiveQueueSeconds(queueTimeoutSeconds, _billingOptions.UserQueueMaxSeconds, trusted);
        var maxWaiting = BillingOptions.MaxWaiting(limit, _billingOptions.UserQueueLengthFactor, trusted);
        var waitTimeout = queueSeconds > 0
            ? TimeSpan.FromSeconds(queueSeconds)
            : TimeSpan.Zero;

        // The finally releases exactly what was taken.
        var userSlotAcquired = false;
        var globalSlotAcquired = false;

        try
        {
            // 1) The user's own slot, waiting in the user's queue. No global slot is held here.
            int slot;
            try
            {
                slot = await gate.WaitAsync(waitTimeout, maxWaiting, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The client went away while queued; nobody is left to answer.
                return;
            }
            userSlotAcquired = slot > 0;

            if (!userSlotAcquired)
            {
                string cause;
                if (slot == UserGate.QueueFull)
                {
                    cause = "queue full";
                    Interlocked.Increment(ref _rejectedQueueFull);
                }
                else if (queueSeconds > 0)
                {
                    cause = "queue timeout";
                    Interlocked.Increment(ref _rejectedTimeout);
                }
                else
                {
                    cause = "no queue";
                    Interlocked.Increment(ref _rejectedNoQueue);
                }
                _logger.LogWarning("User {UserId} exceeded concurrency limit ({Limit}): {Cause}, queue wait {QueueSeconds}s",
                    userId.Value, limit, cause, queueSeconds);
                var retryAfter = BillingOptions.RetryAfterSeconds(queueSeconds);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers["Retry-After"] = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var factory = context.RequestServices.GetRequiredService<IStringLocalizerFactory>();
                var localizer = factory.Create(typeof(SharedStrings));
                await context.Response.WriteAsJsonAsync(new
                {
                    message = localizer["TooManyRequests"].Value,
                    limit,
                    retryAfter,
                });
                return;
            }

            // 2) The global slot after a short wait, so the time spent in the user's queue is not wasted.
            if (_global.Enabled)
            {
                try
                {
                    globalSlotAcquired = await _global.TryEnterAsync(_globalWait, context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    return;
                }
                if (!globalSlotAcquired)
                {
                    _logger.LogWarning("Global concurrency limit ({Limit}) reached", _global.Limit);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers["Retry-After"] = "10";
                    var factory = context.RequestServices.GetRequiredService<IStringLocalizerFactory>();
                    var localizer = factory.Create(typeof(SharedStrings));
                    await context.Response.WriteAsJsonAsync(new
                    {
                        code = "SERVER_AT_CAPACITY",
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
            if (globalSlotAcquired) _global.Release();
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
