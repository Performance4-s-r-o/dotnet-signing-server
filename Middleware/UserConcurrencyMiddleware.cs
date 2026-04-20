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
/// </summary>
public class UserConcurrencyMiddleware
{
    public const string TierItemKey = "ConcurrencyTier";

    private sealed class SemaphoreEntry
    {
        public int Capacity { get; }
        public SemaphoreSlim Semaphore { get; }
        public int InFlight; // mutated via Interlocked

        public SemaphoreEntry(int capacity)
        {
            Capacity = capacity;
            Semaphore = new SemaphoreSlim(capacity, capacity);
        }
    }

    private static readonly ConcurrentDictionary<Guid, SemaphoreEntry> Semaphores = new();

    private const int LimitCacheDurationSeconds = 60;
    private static readonly ConcurrentDictionary<Guid, (int Limit, DateTimeOffset ExpiresAt)> LimitCache = new();

    /// <summary>
    /// Returns the number of in-flight requests for the given user. Kept for diagnostics.
    /// Billing should read the tier from HttpContext.Items[TierItemKey] instead — it is atomic
    /// with semaphore acquisition, unlike this live counter which drifts during request processing.
    /// </summary>
    public static int GetInFlightCount(Guid userId)
    {
        if (!Semaphores.TryGetValue(userId, out var entry))
        {
            return 0;
        }
        return Math.Max(0, Volatile.Read(ref entry.InFlight));
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

    public UserConcurrencyMiddleware(
        RequestDelegate next,
        ILogger<UserConcurrencyMiddleware> logger,
        IOptions<BillingOptions> billingOptions)
    {
        _next = next;
        _logger = logger;
        _billingOptions = billingOptions.Value;
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

        var limit = await GetUserLimit(userId.Value, context.RequestServices);

        var entry = Semaphores.AddOrUpdate(
            userId.Value,
            _ => new SemaphoreEntry(limit),
            (_, existing) => existing.Capacity == limit ? existing : new SemaphoreEntry(limit));

        if (!await entry.Semaphore.WaitAsync(TimeSpan.Zero))
        {
            _logger.LogWarning("User {UserId} exceeded concurrency limit ({Limit})", userId.Value, limit);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = "5";
            var factory = context.RequestServices.GetRequiredService<IStringLocalizerFactory>();
            var localizer = factory.Create(typeof(SharedStrings));
            await context.Response.WriteAsJsonAsync(new
            {
                message = localizer["TooManyRequests"].Value,
                limit,
                retryAfter = 5,
            });
            return;
        }

        // Atomic: this request's slot is determined right here, not later at debit time.
        var slot = Interlocked.Increment(ref entry.InFlight);
        var tier = ComputeTier(slot, _billingOptions.ConcurrencyTierSize, _billingOptions.MaxConcurrencyTier);
        context.Items[TierItemKey] = tier;

        try
        {
            await _next(context);
        }
        finally
        {
            Interlocked.Decrement(ref entry.InFlight);
            entry.Semaphore.Release();
        }
    }

    private static Guid? ResolveUserId(HttpContext context)
    {
        var claim = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                    ?? context.User?.FindFirst("sub")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static async Task<int> GetUserLimit(Guid userId, IServiceProvider services)
    {
        if (LimitCache.TryGetValue(userId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Limit;
        }

        var defaultLimit = services.GetService<IOptions<BillingOptions>>()?.Value.ConcurrencyDefaultLimit ?? 3;

        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.MaxConcurrentOperations)
                .FirstOrDefaultAsync();

            var limit = user ?? defaultLimit;
            LimitCache[userId] = (limit, DateTimeOffset.UtcNow.AddSeconds(LimitCacheDurationSeconds));
            return limit;
        }
        catch
        {
            return defaultLimit;
        }
    }
}
