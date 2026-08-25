using DotNetSigningServer.Services;

namespace DotNetSigningServer.Tests.Services;

public class SecureTokensTests
{
    [Fact]
    public void Generate_Produces256BitsOfHex()
    {
        Assert.Equal(64, SecureTokens.Generate().Length);
    }

    [Fact]
    public void Generate_ProducesDistinctValues()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => SecureTokens.Generate()).ToHashSet();
        Assert.Equal(200, tokens.Count);
    }

    [Fact]
    public void Hash_IsDeterministicAndDiffersFromToken()
    {
        var token = SecureTokens.Generate();
        var hash = SecureTokens.Hash(token);

        Assert.Equal(hash, SecureTokens.Hash(token));
        Assert.NotEqual(token, hash);
    }

    [Fact]
    public void Hash_FitsThe128CharTokenColumns()
    {
        Assert.Equal(64, SecureTokens.Hash(SecureTokens.Generate()).Length);
    }

    [Fact]
    public void Hash_DistinctTokensProduceDistinctHashes()
    {
        Assert.NotEqual(SecureTokens.Hash("token-a"), SecureTokens.Hash("token-b"));
    }

    [Fact]
    public void Hash_MatchesKnownSha256UppercaseHex()
    {
        // Pins the encoding the migration's upper(encode(sha256(...), 'hex')) relies on.
        Assert.Equal(
            "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
            SecureTokens.Hash("abc"));
    }
}
