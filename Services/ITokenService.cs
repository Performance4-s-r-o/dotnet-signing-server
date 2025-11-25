using DotNetSigningServer.Models;

namespace DotNetSigningServer.Services;

public interface ITokenService
{
    (string PlaintextToken, byte[] TokenHash, DateTimeOffset? ExpiresAt) IssueToken(User user, string label, DateTimeOffset? expiresAt = null);
    byte[] HashToken(string token);
}
