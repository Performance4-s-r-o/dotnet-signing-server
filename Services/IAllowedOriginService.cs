using DotNetSigningServer.Models;

namespace DotNetSigningServer.Services;

public interface IAllowedOriginService
{
    bool IsOriginAllowed(string origin, HttpContext httpContext);
    bool IsOriginAllowedForToken(string origin, ApiToken token);
    bool IsLocalOrigin(string origin);

    /// <summary>Whether <paramref name="origin"/> is the origin the request was addressed to.</summary>
    bool IsOwnOrigin(string origin, HttpContext httpContext);

    /// <summary>Forgets the cached browser-token origins (after a token is created or revoked).</summary>
    void InvalidateTokenOrigins();
}
