using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace DotNetSigningServer.Services;

/// <summary>
/// Which authentication scheme the default "SmartAuth" policy scheme forwards to.
/// An API key (Authorization: Bearer) is accepted on the REST surface only; every
/// other path — the account web UI — authenticates by the session cookie alone, so a
/// leaked API token cannot mint new tokens, change billing or account settings.
/// </summary>
public static class AuthenticationSchemeSelection
{
    public const string PolicySchemeName = "SmartAuth";

    public static string Select(HttpContext context)
    {
        if (IsApiPath(context.Request.Path))
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrWhiteSpace(authHeader) &&
                authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return ApiKeyAuthenticationHandler.SchemeName;
            }
        }
        return CookieAuthenticationDefaults.AuthenticationScheme;
    }

    /// <summary>
    /// "/api" and below. The culture prefix ("/cs") has already been moved into PathBase
    /// by <c>CulturePrefixMiddleware</c>, so this sees the routed path.
    /// </summary>
    public static bool IsApiPath(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// <c>[Authorize]</c> for the account web UI: only the session cookie can satisfy it,
/// whatever the default scheme forwards to. Use this on every MVC controller/action
/// instead of a bare <c>[Authorize]</c> (a test enforces it).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class CookieAuthorizeAttribute : AuthorizeAttribute
{
    public CookieAuthorizeAttribute()
    {
        AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme;
    }

    public CookieAuthorizeAttribute(string policy) : this()
    {
        Policy = policy;
    }
}

/// <summary>
/// Marks an /api action that the web UI calls from the browser with the session
/// cookie (the template builder). Every other /api action accepts the API key only;
/// a cookie session there is answered 401.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AllowCookieSessionAttribute : Attribute
{
}
