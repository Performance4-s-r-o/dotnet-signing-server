using DotNetSigningServer.Models;

namespace DotNetSigningServer.Services;

public interface IApiAuthService
{
    Task<User?> ValidateTokenAsync(string authorizationHeader, string? originHeader = null);
}
