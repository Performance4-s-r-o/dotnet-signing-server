using System.Security.Cryptography;
using System.Text;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services;

public class TokenService : ITokenService
{
    private readonly byte[] _secret;

    public TokenService(IOptions<TokenOptions> options)
    {
        var secret = options.Value.Secret;
        if (string.IsNullOrWhiteSpace(secret) || string.Equals(secret, "change-this-secret", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("TokenOptions.Secret must be configured with a strong value.");
        }

        _secret = Encoding.UTF8.GetBytes(secret);
    }

    public (string PlaintextToken, byte[] TokenHash, DateTimeOffset? ExpiresAt) IssueToken(User user, string label, DateTimeOffset? expiresAt = null)
    {
        byte[] tokenBytes = new byte[32];
        RandomNumberGenerator.Fill(tokenBytes);
        string token = Convert.ToBase64String(tokenBytes);
        byte[] hash = HashToken(token);
        return (token, hash, expiresAt);
    }

    public byte[] HashToken(string token)
    {
        using var hmac = new HMACSHA256(_secret);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(token));
    }

    public bool VerifyToken(string token, byte[] storedHash)
    {
        var computed = HashToken(token);
        return CryptographicOperations.FixedTimeEquals(computed, storedHash);
    }
}
