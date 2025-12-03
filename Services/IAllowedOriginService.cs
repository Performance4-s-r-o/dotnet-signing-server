using DotNetSigningServer.Models;

namespace DotNetSigningServer.Services;

public interface IAllowedOriginService
{
    bool IsOriginAllowed(string origin, HttpContext httpContext);
    bool IsOriginAllowedForToken(string origin, ApiToken token);
    bool IsLocalOrigin(string origin);
}
