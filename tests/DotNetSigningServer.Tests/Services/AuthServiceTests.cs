using DotNetSigningServer.Services;

namespace DotNetSigningServer.Tests.Services;

public class AuthServiceTests
{
    private readonly AuthService _sut = new();

    [Fact]
    public void HashPassword_ReturnsHashOf32Bytes()
    {
        var (hash, _, _) = _sut.HashPassword("password123");
        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void HashPassword_ReturnsSaltOf16Bytes()
    {
        var (_, salt, _) = _sut.HashPassword("password123");
        Assert.Equal(16, salt.Length);
    }

    [Fact]
    public void HashPassword_UsesCurrentIterationCount()
    {
        var (_, _, iterations) = _sut.HashPassword("password123");
        Assert.Equal(AuthService.DefaultIterations, iterations);
        Assert.True(iterations >= 600_000, "New hashes must meet the OWASP 2023 PBKDF2-HMAC-SHA256 baseline.");
    }

    [Fact]
    public void HashPassword_ProducesDifferentSaltsEachCall()
    {
        var (_, salt1, _) = _sut.HashPassword("password123");
        var (_, salt2, _) = _sut.HashPassword("password123");
        Assert.NotEqual(salt1, salt2);
    }

    [Fact]
    public void HashPassword_ProducesDifferentHashesEachCall()
    {
        var (hash1, _, _) = _sut.HashPassword("password123");
        var (hash2, _, _) = _sut.HashPassword("password123");
        // Different salts → different hashes
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void VerifyPassword_ReturnsTrueForCorrectPassword()
    {
        var (hash, salt, iterations) = _sut.HashPassword("correct-password");
        Assert.True(_sut.VerifyPassword("correct-password", hash, salt, iterations));
    }

    [Fact]
    public void VerifyPassword_ReturnsFalseForWrongPassword()
    {
        var (hash, salt, iterations) = _sut.HashPassword("correct-password");
        Assert.False(_sut.VerifyPassword("wrong-password", hash, salt, iterations));
    }

    [Fact]
    public void VerifyPassword_ReturnsFalseForTamperedHash()
    {
        var (hash, salt, iterations) = _sut.HashPassword("password");
        hash[0] ^= 0xFF; // flip bits
        Assert.False(_sut.VerifyPassword("password", hash, salt, iterations));
    }

    [Fact]
    public void VerifyPassword_ReturnsFalseForTamperedSalt()
    {
        var (hash, salt, iterations) = _sut.HashPassword("password");
        salt[0] ^= 0xFF;
        Assert.False(_sut.VerifyPassword("password", hash, salt, iterations));
    }

    [Fact]
    public void VerifyPassword_ReturnsFalseWhenIterationCountDoesNotMatch()
    {
        var (hash, salt, iterations) = _sut.HashPassword("password");
        Assert.False(_sut.VerifyPassword("password", hash, salt, iterations / 2));
    }

    [Fact]
    public void VerifyPassword_ZeroIterations_FallsBackToLegacyCount()
    {
        // Rows created before per-record counts existed store 0 and must keep verifying.
        var salt = new byte[16];
        Random.Shared.NextBytes(salt);
        var legacyHash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            "legacy-password", salt, AuthService.LegacyIterations,
            System.Security.Cryptography.HashAlgorithmName.SHA256, 32);

        Assert.True(_sut.VerifyPassword("legacy-password", legacyHash, salt, 0));
        Assert.False(_sut.VerifyPassword("other-password", legacyHash, salt, 0));
    }

    [Fact]
    public void HashPassword_WorksWithEmptyPassword()
    {
        var (hash, salt, iterations) = _sut.HashPassword("");
        Assert.Equal(32, hash.Length);
        Assert.Equal(16, salt.Length);
        Assert.True(_sut.VerifyPassword("", hash, salt, iterations));
    }

    [Fact]
    public void HashPassword_WorksWithLongPassword()
    {
        var longPassword = new string('x', 10_000);
        var (hash, salt, iterations) = _sut.HashPassword(longPassword);
        Assert.True(_sut.VerifyPassword(longPassword, hash, salt, iterations));
    }
}
