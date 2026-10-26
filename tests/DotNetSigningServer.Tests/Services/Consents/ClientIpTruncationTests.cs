using DotNetSigningServer.Services.Consents;

namespace DotNetSigningServer.Tests.Services.Consents;

public class ClientIpTruncationTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.0")]
    [InlineData("10.1.2.255", "10.1.2.0")]
    [InlineData(" 203.0.113.7 ", "203.0.113.0")]
    [InlineData("2001:db8:abcd:1234:5678:9abc:def0:1234", "2001:db8:abcd::")]
    [InlineData("[2001:db8:abcd:1234::1]", "2001:db8:abcd::")]
    [InlineData("fe80::1%eth0", "fe80::")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.0")]
    [InlineData("::1", "::")]
    public void Truncates(string input, string expected) =>
        Assert.Equal(expected, ClientIpTruncation.Truncate(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    [InlineData("203.0.113.7.9")]
    [InlineData("999.1.1.1")]
    public void Malformed_ReturnsNull(string? input) =>
        Assert.Null(ClientIpTruncation.Truncate(input));
}
