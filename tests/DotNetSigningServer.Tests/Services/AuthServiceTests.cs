using DotNetSigningServer.Services;

namespace DotNetSigningServer.Tests.Services;

public class AuthServiceTests
{
    private readonly AuthService _sut = new();

    [Fact]
    public void HashPassword_ReturnsHashOf32Bytes()
    {
        var (hash, _) = _sut.HashPassword("password123");
        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void HashPassword_ReturnsSaltOf16Bytes()
    {
        var (_, salt) = _sut.HashPassword("password123");
        Assert.Equal(16, salt.Length);
    }

    [Fact]
    public void HashPassword_ProducesDifferentSaltsEachCall()
    {
        var (_, salt1) = _sut.HashPassword("password123");
        var (_, salt2) = _sut.HashPassword("password123");
        Assert.NotEqual(salt1, salt2);
    }

    [Fact]
    public void HashPassword_ProducesDifferentHashesEachCall()
    {
        var (hash1, _) = _sut.HashPassword("password123");
        var (hash2, _) = _sut.HashPassword("password123");
        // Different salts → different hashes
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void VerifyPassword_ReturnsTrueForCorrectPassword()
    {
        var (hash, salt) = _sut.HashPassword("correct-password");
        Assert.True(_sut.VerifyPassword("correct-password", hash, salt));
    }

    [Fact]
    public void VerifyPassword_ReturnsFalseForWrongPassword()
    {
        var (hash, salt) = _sut.HashPassword("correct-password");
        Assert.False(_sut.VerifyPassword("wrong-password", hash, salt));
    }

    [Fact]
    public void VerifyPassword_ReturnsFalseForTamperedHash()
    {
        var (hash, salt) = _sut.HashPassword("password");
        hash[0] ^= 0xFF; // flip bits
        Assert.False(_sut.VerifyPassword("password", hash, salt));
    }

    [Fact]
    public void VerifyPassword_ReturnsFalseForTamperedSalt()
    {
        var (hash, salt) = _sut.HashPassword("password");
        salt[0] ^= 0xFF;
        Assert.False(_sut.VerifyPassword("password", hash, salt));
    }

    [Fact]
    public void HashPassword_WorksWithEmptyPassword()
    {
        var (hash, salt) = _sut.HashPassword("");
        Assert.Equal(32, hash.Length);
        Assert.Equal(16, salt.Length);
        Assert.True(_sut.VerifyPassword("", hash, salt));
    }

    [Fact]
    public void HashPassword_WorksWithLongPassword()
    {
        var longPassword = new string('x', 10_000);
        var (hash, salt) = _sut.HashPassword(longPassword);
        Assert.True(_sut.VerifyPassword(longPassword, hash, salt));
    }
}
