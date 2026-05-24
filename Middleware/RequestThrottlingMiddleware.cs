using System.Collections.Concurrent;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Middleware;

public class RequestThrottlingMiddleware
{
    private static readonly ConcurrentDictionary<string, int> InFlightCounts = new();

    private readonly RequestDelegate _next;
    private readonly LimitsOptions _options;
    private readonly ILogger<RequestThrottlingMiddleware> _logger;

    public RequestThrottlingMiddleware(RequestDelegate next, IOptions<LimitsOptions> options, ILogger<RequestThrottlingMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Throttle only API routes
        if (!context.Request.Path.HasValue || !context.Request.Path.Value!.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var key = ResolveKey(context);
        var current = InFlightCounts.AddOrUpdate(key, 1, (_, val) => val + 1);

        if (current > _options.MaxConcurrentRequestsPerKey)
        {
            Decrement(key);
            _logger.LogWarning("Too many concurrent requests for key {Key} ({Count}/{Max})", key, current, _options.MaxConcurrentRequestsPerKey);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            var factory = context.RequestServices.GetRequiredService<IStringLocalizerFactory>();
            var localizer = factory.Create(typeof(SharedStrings));
            await context.Response.WriteAsJsonAsync(new { message = localizer["TooManyRequests"].Value });
            return;
        }

        try
        {
            await _next(context);
        }
        finally
        {
            Decrement(key);
        }
    }

    private static void Decrement(string key)
    {
        var newVal = InFlightCounts.AddOrUpdate(key, 0, (_, val) => Math.Max(0, val - 1));
        if (newVal == 0)
        {
            // Atomic compare-and-remove: only removes if the value is STILL 0, so a
            // concurrent increment (which set it >0) is never lost. Keeps the dict
            // from growing unbounded by distinct key (per-IP/per-user).
            InFlightCounts.TryRemove(new System.Collections.Generic.KeyValuePair<string, int>(key, 0));
        }
    }

    private static string ResolveKey(HttpContext context)
    {
        var userId = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                     ?? context.User?.FindFirst("sub")?.Value;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            return $"user:{userId}";
        }

        var ip = context.Connection.RemoteIpAddress?.ToString();
        return !string.IsNullOrWhiteSpace(ip) ? $"ip:{ip}" : "anonymous";
    }
}
