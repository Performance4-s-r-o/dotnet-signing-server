using System.Threading.RateLimiting;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace DotNetSigningServer.Middleware;

/// <summary>
/// ASP.NET rate limiter for #73. One chained global limiter; each link answers only for the
/// requests it is about and is a no-op for the rest, so a request is counted against every
/// limit that applies to it.
/// </summary>
public static class RateLimiting
{
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("RateLimits").Get<RateLimitOptions>() ?? new RateLimitOptions();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (context, token) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                {
                    context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retry.TotalSeconds).ToString("0");
                }
                else
                {
                    context.HttpContext.Response.Headers.RetryAfter = "60";
                }
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { code = "RATE_LIMITED", message = "Too many requests." }, token);
            };

            if (!options.Enabled)
            {
                limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetNoLimiter("off"));
                return;
            }

            limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                Link(c => IsApi(c) && !IsAuthenticated(c) && !IsAnonymousApi(c),
                    c => $"unauth:{ClientIp.Of(c)}", options.UnauthenticatedPerMinute, TimeSpan.FromMinutes(1)),
                Link(c => IsApi(c) && IsAuthenticated(c),
                    c => $"key:{UserKey(c)}", options.PerKeyPerMinute, TimeSpan.FromMinutes(1)),
                Link(c => PathIs(c, "/api/tsa-probe"),
                    c => $"probe:{UserKey(c)}", options.TsaProbePerMinute, TimeSpan.FromMinutes(1)),
                Link(c => PathIs(c, "/api/inspect-signatures"),
                    c => $"inspect:{UserKey(c)}", options.InspectSignaturesPerMinute, TimeSpan.FromMinutes(1)),
                Link(c => PathIs(c, "/api/timestamp-digest"),
                    c => $"tsdigest:{UserKey(c)}", options.TimestampDigestPerMinute, TimeSpan.FromMinutes(1)),
                Link(c => c.Request.Method == "POST" && PathIs(c, "/Account/SignUp"),
                    c => $"signup:{ClientIp.Of(c)}", options.SignUpPerHour, TimeSpan.FromHours(1)),
                Link(c => c.Request.Method == "POST" && AuthPaths.Any(p => PathIs(c, p)),
                    c => $"auth:{ClientIp.Of(c)}", options.AuthPer10Minutes, TimeSpan.FromMinutes(10)));
        });
    }

    private static readonly string[] AuthPaths =
    {
        "/Account/SignIn", "/Account/TwoFactor", "/Account/ForgotPassword", "/Account/ResetPassword", "/Account/Verify",
    };

    private static readonly string[] AnonymousApi = { "/api/code-formats", "/api/docs", "/api/webhooks" };

    private static PartitionedRateLimiter<HttpContext> Link(
        Func<HttpContext, bool> applies, Func<HttpContext, string> key, int permits, TimeSpan window)
        => PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (!applies(context)) return RateLimitPartition.GetNoLimiter("n/a");
            return RateLimitPartition.GetFixedWindowLimiter(key(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, permits),
                Window = window,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        });

    private static bool IsApi(HttpContext c) => AuthenticationSchemeSelection.IsApiPath(c.Request.Path);

    private static bool IsAuthenticated(HttpContext c) => c.User?.Identity?.IsAuthenticated == true;

    private static bool IsAnonymousApi(HttpContext c) =>
        AnonymousApi.Any(p => c.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    private static bool PathIs(HttpContext c, string path) =>
        c.Request.Path.Equals(path, StringComparison.OrdinalIgnoreCase);

    private static string UserKey(HttpContext c) =>
        c.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? c.User?.FindFirst("sub")?.Value
        ?? ClientIp.Of(c);
}
