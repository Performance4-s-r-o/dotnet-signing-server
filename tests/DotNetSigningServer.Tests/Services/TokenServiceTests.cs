using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;

namespace DotNetSigningServer.Tests.Services;

public class TokenServiceTests
{
    private static TokenService CreateService(string secret = "a-strong-test-secret-for-unit-tests")
    {
        return TestHelpers.CreateTokenService(secret);
    }

    [Fact]
    public void Constructor_ThrowsWhenSecretIsDefault_InProduction()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TestHelpers.CreateTokenService("change-this-secret", "Production"));
    }

    [Fact]
    public void Constructor_ThrowsWhenSecretIsEmpty_InProduction()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TestHelpers.CreateTokenService("", "Production"));
    }

    [Fact]
    public void Constructor_ThrowsWhenSecretIsWhitespace_InProduction()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TestHelpers.CreateTokenService("   ", "Production"));
    }

    [Fact]
    public void Constructor_AutoGeneratesSecret_InDevelopment()
    {
        var sut = TestHelpers.CreateTokenService("weak");
        Assert.NotNull(sut);
    }

    [Fact]
    public void Constructor_SucceedsWithValidSecret()
    {
        var sut = CreateService();
        Assert.NotNull(sut);
    }

    [Fact]
    public void IssueToken_ReturnsNonEmptyPlaintextToken()
    {
        var sut = CreateService();
        var user = TestHelpers.CreateTestUser();
        var (token, _, _) = sut.IssueToken(user, "test");
        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public void IssueToken_ReturnsNonEmptyHash()
    {
        var sut = CreateService();
        var user = TestHelpers.CreateTestUser();
        var (_, hash, _) = sut.IssueToken(user, "test");
        Assert.NotEmpty(hash);
        Assert.Equal(32, hash.Length); // SHA256 = 32 bytes
    }

    [Fact]
    public void IssueToken_ReturnsUniqueTokensPerCall()
    {
        var sut = CreateService();
        var user = TestHelpers.CreateTestUser();
        var (token1, _, _) = sut.IssueToken(user, "test");
        var (token2, _, _) = sut.IssueToken(user, "test");
        Assert.NotEqual(token1, token2);
    }

    [Fact]
    public void IssueToken_PassesThroughExpiresAt()
    {
        var sut = CreateService();
        var user = TestHelpers.CreateTestUser();
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        var (_, _, expiresAt) = sut.IssueToken(user, "test", expires);
        Assert.Equal(expires, expiresAt);
    }

    [Fact]
    public void IssueToken_ExpiresAtIsNullByDefault()
    {
        var sut = CreateService();
        var user = TestHelpers.CreateTestUser();
        var (_, _, expiresAt) = sut.IssueToken(user, "test");
        Assert.Null(expiresAt);
    }

    [Fact]
    public void HashToken_IsDeterministic()
    {
        var sut = CreateService();
        var hash1 = sut.HashToken("some-token");
        var hash2 = sut.HashToken("some-token");
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void HashToken_DifferentInputsProduceDifferentHashes()
    {
        var sut = CreateService();
        var hash1 = sut.HashToken("token-a");
        var hash2 = sut.HashToken("token-b");
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void HashToken_DifferentSecretsProduceDifferentHashes()
    {
        var sut1 = CreateService("secret-one-for-testing-long-enough!");
        var sut2 = CreateService("secret-two-for-testing-long-enough!");
        var hash1 = sut1.HashToken("same-token");
        var hash2 = sut2.HashToken("same-token");
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void VerifyToken_ReturnsTrueForMatchingToken()
    {
        var sut = CreateService();
        var user = TestHelpers.CreateTestUser();
        var (token, hash, _) = sut.IssueToken(user, "test");
        Assert.True(sut.VerifyToken(token, hash));
    }

    [Fact]
    public void VerifyToken_ReturnsFalseForWrongToken()
    {
        var sut = CreateService();
        var user = TestHelpers.CreateTestUser();
        var (_, hash, _) = sut.IssueToken(user, "test");
        Assert.False(sut.VerifyToken("wrong-token", hash));
    }

    [Fact]
    public void VerifyToken_ReturnsFalseForTamperedHash()
    {
        var sut = CreateService();
        var user = TestHelpers.CreateTestUser();
        var (token, hash, _) = sut.IssueToken(user, "test");
        hash[0] ^= 0xFF;
        Assert.False(sut.VerifyToken(token, hash));
    }
}
