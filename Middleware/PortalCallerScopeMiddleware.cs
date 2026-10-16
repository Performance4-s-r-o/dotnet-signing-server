using DotNetSigningServer.Services;

namespace DotNetSigningServer.Middleware;

/// <summary>
/// Puts the portal's <c>X-P4-Tenant</c> and <c>X-Request-Id</c> into the log scope of the request
/// (#78), so every line written while serving it can be traced back to a tenant and to the portal's
/// own log. Only for the trusted caller (<see cref="PortalTrust"/>); anybody else's headers are
/// ignored. Runs after authentication.
/// </summary>
public class PortalCallerScopeMiddleware
{
    public const string TenantScopeKey = "TenantId";
    public const string RequestIdScopeKey = "RequestId";

    private readonly RequestDelegate _next;
    private readonly ILogger<PortalCallerScopeMiddleware> _logger;

    public PortalCallerScopeMiddleware(RequestDelegate next, ILogger<PortalCallerScopeMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var caller = PortalTrust.CallerOf(context);
        if (caller.TenantId == null && caller.RequestId == null)
        {
            await _next(context);
            return;
        }

        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            [TenantScopeKey] = caller.TenantId,
            [RequestIdScopeKey] = caller.RequestId,
        }))
        {
            _logger.LogInformation("Portal call {Method} {Path} tenant={TenantId} requestId={RequestId}",
                context.Request.Method, context.Request.Path, caller.TenantId, caller.RequestId);
            await _next(context);
        }
    }
}
