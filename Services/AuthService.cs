using System.Security.Cryptography;

namespace DotNetSigningServer.Services;

public class AuthService : IAuthService
{
    private const int SaltSize = 16;
    private const int KeySize = 32;

    /// <summary>
    /// Iteration count used before per-record counts were stored. Rows with
    /// <c>PasswordIterations &lt;= 0</c> were hashed with this and must keep
    /// verifying until the owner next signs in and gets rehashed.
    /// </summary>
    public const int LegacyIterations = 100_000;

    /// <summary>Iteration count for new hashes (OWASP 2023 for PBKDF2-HMAC-SHA256).</summary>
    public const int DefaultIterations = 600_000;

    public int Iterations => DefaultIterations;

    public (byte[] Hash, byte[] Salt, int Iterations) HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = PBKDF2(password, salt, DefaultIterations);
        return (hash, salt, DefaultIterations);
    }

    public bool VerifyPassword(string password, byte[] hash, byte[] salt, int iterations)
    {
        var computed = PBKDF2(password, salt, ResolveIterations(iterations));
        return CryptographicOperations.FixedTimeEquals(hash, computed);
    }

    /// <summary>Stored counts of 0 (pre-migration rows) mean the legacy count.</summary>
    public static int ResolveIterations(int storedIterations) =>
        storedIterations > 0 ? storedIterations : LegacyIterations;

    private static byte[] PBKDF2(string password, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);
    }
}
