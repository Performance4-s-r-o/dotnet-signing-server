using System.Security.Cryptography;
using System.Text;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services;

public class TokenService : ITokenService
{
    private readonly byte[] _secret;

    public TokenService(IOptions<TokenOptions> options, IWebHostEnvironment env, ILogger<TokenService> logger)
    {
        var secret = options.Value.Secret;
        var isWeak = string.IsNullOrWhiteSpace(secret)
                     || string.Equals(secret, "change-this-secret", StringComparison.Ordinal)
                     || string.Equals(secret, "secret", StringComparison.Ordinal)
                     || secret.Length < 32;

        if (isWeak)
        {
            if (env.IsProduction())
            {
                throw new InvalidOperationException(
                    "TokenOptions.Secret must be configured with a strong value (>= 32 characters) in production. " +
                    "Set the Token__Secret environment variable.");
            }

            // Auto-generate a secure secret for development
            var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            logger.LogWarning("Token secret is weak or missing — auto-generated a random secret for this session. " +
                              "This is acceptable in development but MUST be configured in production.");
            _secret = Encoding.UTF8.GetBytes(generated);
        }
        else
        {
            _secret = Encoding.UTF8.GetBytes(secret);
        }
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
