using DotNetSigningServer.Services;

namespace DotNetSigningServer.Middleware;

/// <summary>
/// Puts the caller's <c>X-Request-Id</c> and the provisioned account's external id into the log
/// scope of an API-key request, so a line can be traced to the integrator's log and tenant.
/// The account comes from the database (the token's account), never from a header. Runs after
/// authentication.
/// </summary>
public class RequestCorrelationMiddleware
{
    public const string AccountScopeKey = "Account";
    public const string RequestIdScopeKey = "RequestId";
    public const string RequestIdHeader = "X-Request-Id";
    public const int MaxRequestIdLength = 128;

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestCorrelationMiddleware> _logger;

    public RequestCorrelationMiddleware(RequestDelegate next, ILogger<RequestCorrelationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var principal = context.User;
        if (principal?.Identity?.IsAuthenticated != true
            || principal.Identity.AuthenticationType != ApiKeyAuthenticationHandler.SchemeName)
        {
            await _next(context);
            return;
        }

        var account = principal.FindFirst(SessionClaims.AccountRef)?.Value;
        var requestId = Clean(context.Request.Headers[RequestIdHeader].ToString(), MaxRequestIdLength);
        if (account == null && requestId == null)
        {
            await _next(context);
            return;
        }

        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            [AccountScopeKey] = account,
            [RequestIdScopeKey] = requestId,
        }))
        {
            await _next(context);
        }
    }

    /// <summary>A value from a safe alphabet within the length, or null: it goes into logs.</summary>
    internal static string? Clean(string? raw, int maxLength)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > maxLength) return null;
        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')) return null;
        }
        return value;
    }
}
