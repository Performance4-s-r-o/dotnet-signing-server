using System.Security.Cryptography;
using System.Text;

namespace DotNetSigningServer.Services;

/// <summary>
/// Single-use bearer secrets that arrive by email (verification, password reset,
/// auto-recharge cancel links).
///
/// <c>Guid.NewGuid()</c> is not a documented CSPRNG — it guarantees uniqueness, not
/// unpredictability — and these tokens are the only credential standing between an
/// attacker and an account takeover. They are generated from
/// <see cref="RandomNumberGenerator"/> and stored as a SHA-256 hash so a database
/// dump cannot be replayed against the live site.
/// </summary>
public static class SecureTokens
{
    private const int TokenBytes = 32; // 256 bits

    /// <summary>Generates a new token. Email the plaintext, persist <see cref="Hash"/>.</summary>
    public static string Generate() => Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>Lookup key for a presented token. 64 hex chars, fits the 128-char columns.</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Compares a presented secret against a stored hash without leaking, through
    /// how long the comparison took, how much of it matched.
    ///
    /// With an attempt limiter in front, a six-digit code is not realistically
    /// attackable this way — but the limiter is the thing that has to hold, and
    /// constant-time comparison costs nothing.
    /// </summary>
    public static bool MatchesHash(string? presented, string? storedHash)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(storedHash)) return false;

        var candidate = Encoding.UTF8.GetBytes(Hash(presented.Trim()));
        var expected = Encoding.UTF8.GetBytes(storedHash);
        // FixedTimeEquals throws on unequal lengths; a stored value that is not a
        // hash is a mismatch, not a crash.
        return candidate.Length == expected.Length
               && CryptographicOperations.FixedTimeEquals(candidate, expected);
    }
}
