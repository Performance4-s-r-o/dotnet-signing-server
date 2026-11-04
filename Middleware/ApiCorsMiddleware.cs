using DotNetSigningServer.Services;

namespace DotNetSigningServer.Middleware;

/// <summary>
/// CORS on <c>/api</c> (#177). A preflight carries no token, so it is decided by
/// <see cref="IAllowedOriginService.IsOriginAllowed"/>: an origin some active browser token lists,
/// the server's own origin, or localhost in Development; <c>Cors:AllowedOrigins</c> only narrows
/// it. The real request is decided by the token itself (origin lock, browser scope, credit limit)
/// and carries the CORS headers, so a refusal is readable in the browser.
///
/// <c>Access-Control-Allow-Credentials</c> is never sent, and the session cookie is dropped from
/// every request from another origin: a site whose origin passes here (any user can name one on a
/// browser token) still cannot ride a signed-in session on the cookie endpoints of <c>/api</c>.
/// </summary>
public sealed class ApiCorsMiddleware(RequestDelegate next, ILogger<ApiCorsMiddleware> logger)
{
    public const string AllowHeaders = "Authorization,Content-Type,Idempotency-Key";
    public const string ExposeHeaders = "X-Operation-Id,X-Operation-State,Idempotent-Replayed,Retry-After,X-Signature-Verified";
    public const string AllowMethods = "GET,POST,PUT,DELETE,OPTIONS";

    public async Task InvokeAsync(HttpContext context, IAllowedOriginService origins)
    {
        // A prefix, as the shim always matched: /api-docs and /ApiTokens are covered too.
        if (!(context.Request.Path.Value ?? "").StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrWhiteSpace(origin))
        {
            if (!origins.IsOriginAllowed(origin, context))
            {
                logger.LogWarning("CORS origin not allowed: {Origin}", origin);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            // A cross-origin call authenticates by its token only, never by the session cookie.
            if (!origins.IsOwnOrigin(origin, context))
            {
                context.Request.Headers.Remove("Cookie");
            }

            context.Response.Headers["Access-Control-Allow-Origin"] = origin;
            context.Response.Headers["Vary"] = "Origin";
            // No billing-bypass header (#78): only the portal sends it, server to server, never a browser.
            context.Response.Headers["Access-Control-Allow-Headers"] = AllowHeaders;
            context.Response.Headers["Access-Control-Expose-Headers"] = ExposeHeaders;
            context.Response.Headers["Access-Control-Allow-Methods"] = AllowMethods;
        }

        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        await next(context);
    }
}
