using System.Net;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;

namespace DotNetSigningServer.Tests.Services;

public class IpWhitelistServiceTests
{
    private readonly IpWhitelistService _sut = new();

    private static ApiToken TokenWithIps(string? allowedIps) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Label = "test",
        TokenHash = new byte[] { 1 },
        AllowedIps = allowedIps
    };

    // --- IsIpAllowedForToken ---

    [Fact]
    public void IsIpAllowedForToken_NullAllowedIps_ReturnsTrue()
    {
        var token = TokenWithIps(null);
        Assert.True(_sut.IsIpAllowedForToken(IPAddress.Parse("1.2.3.4"), token));
    }

    [Fact]
    public void IsIpAllowedForToken_EmptyAllowedIps_ReturnsTrue()
    {
        var token = TokenWithIps("  ");
        Assert.True(_sut.IsIpAllowedForToken(IPAddress.Parse("1.2.3.4"), token));
    }

    [Fact]
    public void IsIpAllowedForToken_NullClientIp_WhenRestricted_ReturnsFalse()
    {
        var token = TokenWithIps("10.0.0.1");
        Assert.False(_sut.IsIpAllowedForToken(null, token));
    }

    [Fact]
    public void IsIpAllowedForToken_ExactMatch_ReturnsTrue()
    {
        var token = TokenWithIps("10.0.0.1");
        Assert.True(_sut.IsIpAllowedForToken(IPAddress.Parse("10.0.0.1"), token));
    }

    [Fact]
    public void IsIpAllowedForToken_NoMatch_ReturnsFalse()
    {
        var token = TokenWithIps("10.0.0.1");
        Assert.False(_sut.IsIpAllowedForToken(IPAddress.Parse("10.0.0.2"), token));
    }

    [Fact]
    public void IsIpAllowedForToken_CidrMatch_ReturnsTrue()
    {
        var token = TokenWithIps("192.168.1.0/24");
        Assert.True(_sut.IsIpAllowedForToken(IPAddress.Parse("192.168.1.55"), token));
    }

    [Fact]
    public void IsIpAllowedForToken_CidrNoMatch_ReturnsFalse()
    {
        var token = TokenWithIps("192.168.1.0/24");
        Assert.False(_sut.IsIpAllowedForToken(IPAddress.Parse("192.168.2.1"), token));
    }

    [Fact]
    public void IsIpAllowedForToken_IPv4MappedIPv6_MatchesIPv4()
    {
        var token = TokenWithIps("10.0.0.1");
        // ::ffff:10.0.0.1 is the IPv4-mapped IPv6 form
        var mappedIp = IPAddress.Parse("10.0.0.1").MapToIPv6();
        Assert.True(_sut.IsIpAllowedForToken(mappedIp, token));
    }

    [Fact]
    public void IsIpAllowedForToken_MultipleEntries_MatchesAny()
    {
        var token = TokenWithIps("10.0.0.1\n172.16.0.0/12\n192.168.1.100");
        Assert.True(_sut.IsIpAllowedForToken(IPAddress.Parse("172.16.5.5"), token));
        Assert.True(_sut.IsIpAllowedForToken(IPAddress.Parse("10.0.0.1"), token));
        Assert.True(_sut.IsIpAllowedForToken(IPAddress.Parse("192.168.1.100"), token));
        Assert.False(_sut.IsIpAllowedForToken(IPAddress.Parse("8.8.8.8"), token));
    }

    [Fact]
    public void IsIpAllowedForToken_IPv6Address_ExactMatch()
    {
        var token = TokenWithIps("2001:db8::1");
        Assert.True(_sut.IsIpAllowedForToken(IPAddress.Parse("2001:db8::1"), token));
        Assert.False(_sut.IsIpAllowedForToken(IPAddress.Parse("2001:db8::2"), token));
    }

    // --- ParseAndValidateIps ---

    [Fact]
    public void ParseAndValidateIps_Null_ReturnsEmpty()
    {
        var (valid, invalid) = _sut.ParseAndValidateIps(null);
        Assert.Empty(valid);
        Assert.Empty(invalid);
    }

    [Fact]
    public void ParseAndValidateIps_ValidIps_ReturnsAllValid()
    {
        var (valid, invalid) = _sut.ParseAndValidateIps("10.0.0.1\n192.168.0.0/16");
        Assert.Equal(2, valid.Count);
        Assert.Empty(invalid);
    }

    [Fact]
    public void ParseAndValidateIps_InvalidEntries_ReturnsInvalid()
    {
        var (valid, invalid) = _sut.ParseAndValidateIps("10.0.0.1\nnot-an-ip\n999.999.999.999");
        Assert.Single(valid);
        Assert.Equal(2, invalid.Count);
    }

    [Fact]
    public void ParseAndValidateIps_MixedSeparators_Works()
    {
        var (valid, invalid) = _sut.ParseAndValidateIps("10.0.0.1, 10.0.0.2; 10.0.0.3");
        Assert.Equal(3, valid.Count);
        Assert.Empty(invalid);
    }
}
