using System.Collections.Concurrent;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Middleware;

/// <summary>
/// What /api answers before any controller (and so before any request body is read) — #73.
///
/// 1. No valid credentials on a route that needs them: 401, body untouched. The controllers
///    authenticate inside the action, after model binding has already read and deserialised
///    up to 40 MB of JSON — for anybody.
/// 2. Operator switches (<see cref="OperatorSwitch"/>): a blocked account, a disabled feature.
/// 3. A key that keeps sending requests that fail on the heavy path is blocked for a while:
///    a failed operation is free, so a bomb that always ends in an error could otherwise be
///    repeated for ever.
///
/// Runs after UseAuthentication and after the rate limiter.
/// </summary>
public class ApiGateMiddleware
{
    // The routes that are open on purpose: capability list, documentation, webhooks (signed).
    private static readonly string[] Anonymous =
    {
        "/api/code-formats", "/api/docs", "/api/webhooks/",
    };

    // Routes whose failures count towards the block, and the feature each belongs to.
    private static readonly (string Path, string Feature)[] Heavy =
    {
        ("/api/presign", "sign"), ("/api/sign", "sign"), ("/api/sign-pfx", "sign"),
        ("/api/timestamp", "sign"), ("/api/visual-sign", "sign"), ("/api/seal", "sign"),
        ("/api/attachment", "sign"), ("/api/convert/pdfa", "pdfa"), ("/api/fill-pdf", "fill"),
        ("/api/find-codes", "find-codes"), ("/api/extend-signature", "extend-signature"),
        ("/api/ai/", "ai"),
    };

    private static readonly ConcurrentDictionary<Guid, Failures> FailureLog = new();
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> BlockedUntil = new();
    private static (HashSet<string> Names, DateTimeOffset At) _switches = (new HashSet<string>(), DateTimeOffset.MinValue);

    private readonly RequestDelegate _next;
    private readonly RateLimitOptions _options;

    public ApiGateMiddleware(RequestDelegate next, IOptions<RateLimitOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!AuthenticationSchemeSelection.IsApiPath(path))
        {
            await _next(context);
            return;
        }

        var value = path.Value ?? string.Empty;
        var anonymous = Anonymous.Any(a => value.StartsWith(a, StringComparison.OrdinalIgnoreCase));

        var userId = UserIdOf(context);
        var authenticated = context.User?.Identity?.IsAuthenticated == true;
        if (!anonymous && !authenticated)
        {
            // Nothing is read from the body. The challenge says what is missing.
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await WriteAsync(context, StatusCodes.Status401Unauthorized, "UNAUTHENTICATED");
            return;
        }

        var heavy = Heavy.FirstOrDefault(h => value.StartsWith(h.Path, StringComparison.OrdinalIgnoreCase));

        if (userId is Guid id)
        {
            if (BlockedUntil.TryGetValue(id, out var until))
            {
                if (until > DateTimeOffset.UtcNow)
                {
                    context.Response.Headers.RetryAfter = Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds).ToString("0");
                    await WriteAsync(context, StatusCodes.Status429TooManyRequests, "TOO_MANY_FAILURES");
                    return;
                }
                BlockedUntil.TryRemove(id, out _);
            }

            var names = await SwitchesAsync(context.RequestServices);
            if (names.Contains(OperatorSwitches.User(id)))
            {
                await WriteAsync(context, StatusCodes.Status403Forbidden, "ACCOUNT_BLOCKED");
                return;
            }
            if (heavy.Feature is not null && (names.Contains(OperatorSwitches.Feature(heavy.Feature))
                || (heavy.Feature == "ai" && names.Contains(OperatorSwitches.Feature("ai")))))
            {
                context.Response.Headers.RetryAfter = "60";
                await WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "FEATURE_DISABLED");
                return;
            }
        }

        await _next(context);

        if (userId is Guid user && heavy.Feature is not null && CountsAsFailure(context.Response.StatusCode))
        {
            Record(user);
        }
    }

    /// <summary>400 (a refused input) and 5xx count; 401/402/403/404/409/429 are not the caller's abuse of the heavy path.</summary>
    private static bool CountsAsFailure(int status) => status == 400 || status == 413 || status >= 500;

    private void Record(Guid user)
    {
        var now = DateTimeOffset.UtcNow;
        var log = FailureLog.GetOrAdd(user, _ => new Failures());
        lock (log)
        {
            var window = TimeSpan.FromMinutes(_options.FailureWindowMinutes);
            log.At.RemoveAll(t => now - t > window);
            log.At.Add(now);
            if (log.At.Count >= _options.FailuresBeforeBlock)
            {
                BlockedUntil[user] = now.AddMinutes(_options.BlockMinutes);
                log.At.Clear();
            }
        }
    }

    private sealed class Failures
    {
        public readonly List<DateTimeOffset> At = new();
    }

    private static Guid? UserIdOf(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated != true) return null;
        var claim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                    ?? context.User.FindFirst("sub")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static async Task<HashSet<string>> SwitchesAsync(IServiceProvider services)
    {
        var cached = _switches;
        if (DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(OperatorSwitches.CacheSeconds))
        {
            return cached.Names;
        }

        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var names = await db.OperatorSwitches.AsNoTracking().Select(s => s.Name).ToListAsync();
            _switches = (names.ToHashSet(StringComparer.OrdinalIgnoreCase), DateTimeOffset.UtcNow);
        }
        catch
        {
            // The table is a lever, not a dependency: without it nothing is switched off, and
            // the failure is not retried on every request.
            _switches = (cached.Names, DateTimeOffset.UtcNow);
        }
        return _switches.Names;
    }

    private static async Task WriteAsync(HttpContext context, int status, string code)
    {
        var localizer = context.RequestServices.GetRequiredService<IStringLocalizerFactory>().Create(typeof(SharedStrings));
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { code, message = localizer[$"Error_{code}"].Value });
    }
}
