using System.Security.Claims;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Consents;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Filters;

/// <summary>
/// Re-consent gate (Consents module): a signed-in user whose local consent records do not
/// cover the required versions (<c>docs:meta.required_version</c>) is sent to
/// <c>/Account/Consent?returnUrl=…</c> before any other page. Shadow only logs; Off does nothing.
/// <para>
/// Never on the signing path: <c>/api/*</c>, requests with a Bearer token and anything not
/// signed in with the cookie pass untouched. Also exempt: the consent page itself, signing
/// out, the legal documents, support and the language switch. The state comes from local
/// records only (cached 5 min per user); an error lets the request through.
/// </para>
/// Registered globally except on a PrivateServer.
/// </summary>
public sealed class RequireCurrentConsentFilter : IAsyncActionFilter
{
    public const string ConsentPath = "/Account/Consent";

    private static readonly string[] ExemptPrefixes =
    [
        ConsentPath,
        "/Account/SignOut",
        "/Account/Denied",
        "/Legal",
        "/support",
        "/set-language",
    ];

    private readonly IOptions<P4BackofficeProductOptions> _options;
    private readonly ConsentStatusCache _cache;
    private readonly ILogger<RequireCurrentConsentFilter> _logger;

    public RequireCurrentConsentFilter(
        IOptions<P4BackofficeProductOptions> options,
        ConsentStatusCache cache,
        ILogger<RequireCurrentConsentFilter> logger)
    {
        _options = options;
        _cache = cache;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var mode = _options.Value.ModeFor(BackofficeModule.Consents);
        if (mode == BackofficeMode.Off || !Applies(context.HttpContext) || UserId(context.HttpContext.User) is not { } userId)
        {
            await next();
            return;
        }

        ConsentStatus status;
        try
        {
            var provider = context.HttpContext.RequestServices.GetRequiredService<IConsentStatusProvider>();
            status = await provider.GetAsync(userId, context.HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail open: a broken gate must not lock users out of the product.
            _logger.LogWarning(ex, "[consents] consent state of {UserId} unavailable; request let through", userId);
            await next();
            return;
        }

        if (status.IsCurrent)
        {
            await next();
            return;
        }

        if (mode == BackofficeMode.Shadow)
        {
            if (_cache.ShouldLogShadow(userId))
            {
                _logger.LogInformation(
                    "[consents] shadow: user {UserId} would be asked to confirm {Documents}",
                    userId, string.Join(",", status.Outstanding.Select(o => o.Document)));
            }
            await next();
            return;
        }

        context.Result = new RedirectResult(ConsentUrl(context.HttpContext.Request));
    }

    /// <summary>Whether the gate looks at this request at all. Pure.</summary>
    public static bool Applies(HttpContext http)
    {
        var request = http.Request;
        if (request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)) return false;

        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;

        var identity = http.User?.Identity;
        if (identity?.IsAuthenticated != true
            || !string.Equals(identity.AuthenticationType, CookieAuthenticationDefaults.AuthenticationScheme, StringComparison.Ordinal))
        {
            return false;
        }

        return !ExemptPrefixes.Any(prefix => request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <c>/Account/Consent</c> in the page's language, with the current page as <c>returnUrl</c>
    /// (GET and HEAD only; a form post cannot be replayed).
    /// </summary>
    public static string ConsentUrl(HttpRequest request)
    {
        var target = $"{request.PathBase}{ConsentPath}";
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) return target;
        var returnUrl = $"{request.PathBase}{request.Path}{request.QueryString}";
        return $"{target}?returnUrl={Uri.EscapeDataString(returnUrl)}";
    }

    private static Guid? UserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
