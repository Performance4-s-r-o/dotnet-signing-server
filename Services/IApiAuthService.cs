using System.Net;
using DotNetSigningServer.Models;

namespace DotNetSigningServer.Services;

public interface IApiAuthService
{
    Task<User?> ValidateTokenAsync(string authorizationHeader, string? originHeader = null, IPAddress? clientIp = null);

    /// <summary>
    /// Invalidates all cached validation entries for the given API token.
    /// Call after revoke/delete so subsequent requests re-check the database.
    /// </summary>
    void InvalidateTokenCache(Guid tokenId);
}
