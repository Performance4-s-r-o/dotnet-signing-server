using System.Collections.Concurrent;
using DotNetSigningServer.Options;
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
            InFlightCounts.AddOrUpdate(key, 0, (_, val) => Math.Max(0, val - 1));
            _logger.LogWarning("Too many concurrent requests for key {Key} ({Count}/{Max})", key, current, _options.MaxConcurrentRequestsPerKey);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new { message = "Too many concurrent requests. Please slow down." });
            return;
        }

        try
        {
            await _next(context);
        }
        finally
        {
            InFlightCounts.AddOrUpdate(key, 0, (_, val) => Math.Max(0, val - 1));
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
