using System.Collections.Concurrent;
using DotNetSigningServer.Data;
using DotNetSigningServer.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace DotNetSigningServer.Middleware;

/// <summary>
/// Limits the number of concurrent API operations per authenticated user.
/// Default: 3 concurrent. Configurable per user via MaxConcurrentOperations.
/// When limit is exceeded, returns 429 Too Many Requests.
/// </summary>
public class UserConcurrencyMiddleware
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Semaphores = new();
    private static readonly ConcurrentDictionary<Guid, int> Limits = new();

    private const int DefaultLimit = 3;
    private const int LimitCacheDurationSeconds = 60;
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> LimitCacheExpiry = new();

    private readonly RequestDelegate _next;
    private readonly ILogger<UserConcurrencyMiddleware> _logger;

    public UserConcurrencyMiddleware(RequestDelegate next, ILogger<UserConcurrencyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only apply to API routes
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
        var semaphore = Semaphores.GetOrAdd(userId.Value, _ => new SemaphoreSlim(limit, limit));

        // If limit changed, recreate semaphore
        if (Limits.TryGetValue(userId.Value, out var cachedLimit) && cachedLimit != limit)
        {
            semaphore = new SemaphoreSlim(limit, limit);
            Semaphores[userId.Value] = semaphore;
            Limits[userId.Value] = limit;
        }
        Limits.TryAdd(userId.Value, limit);

        if (!await semaphore.WaitAsync(TimeSpan.Zero))
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

        try
        {
            await _next(context);
        }
        finally
        {
            semaphore.Release();
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
        // Check cache
        if (LimitCacheExpiry.TryGetValue(userId, out var expiry) &&
            expiry > DateTimeOffset.UtcNow &&
            Limits.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        // Fetch from DB
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.MaxConcurrentOperations)
                .FirstOrDefaultAsync();

            var limit = user ?? DefaultLimit;
            Limits[userId] = limit;
            LimitCacheExpiry[userId] = DateTimeOffset.UtcNow.AddSeconds(LimitCacheDurationSeconds);
            return limit;
        }
        catch
        {
            return DefaultLimit;
        }
    }
}
