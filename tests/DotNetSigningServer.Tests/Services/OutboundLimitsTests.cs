using System.Net;
using DotNetSigningServer.Services;

namespace DotNetSigningServer.Tests.Services;

/// <summary>#72: which addresses an outbound call may reach, how much of an answer it reads, how many fetches one operation gets.</summary>
public class OutboundLimitsTests
{
    [Theory]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("64:ff9b::7f00:1")]       // NAT64 to 127.0.0.1
    [InlineData("2002:7f00:1::1")]        // 6to4 embedding 127.0.0.1
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")] // Teredo
    [InlineData("ff02::1")]               // multicast
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]       // IPv4-mapped private
    [InlineData("192.0.0.8")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("169.254.169.254")]
    [InlineData("10.1.2.3")]
    public void Addresses_that_are_not_global_unicast_are_refused(string address)
    {
        Assert.True(PdfCryptoHelper.IsPrivateOrLocalAddress(IPAddress.Parse(address)), address);
    }

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")]
    [InlineData("2a00:1450:4001:80b::200e")]
    public void Public_addresses_pass(string address)
    {
        Assert.False(PdfCryptoHelper.IsPrivateOrLocalAddress(IPAddress.Parse(address)), address);
    }

    [Fact]
    public void An_operation_gets_twenty_fetches()
    {
        using (RevocationFetchPolicy.Begin(maxFetches: 20))
        {
            for (var i = 0; i < 20; i++) Assert.True(RevocationFetchPolicy.TryConsume());
            Assert.False(RevocationFetchPolicy.TryConsume());
        }
        // Outside a scope there is no budget: other callers are not starved by a finished one.
        Assert.True(RevocationFetchPolicy.TryConsume());
    }

    [Fact]
    public void The_budget_expires_with_its_window()
    {
        using (RevocationFetchPolicy.Begin(maxFetches: 100, window: TimeSpan.FromMilliseconds(1)))
        {
            Thread.Sleep(30);
            Assert.False(RevocationFetchPolicy.TryConsume());
        }
    }

    [Fact]
    public void An_oversized_answer_is_refused_while_a_normal_one_is_read()
    {
        var big = new ByteArrayContent(new byte[100]);
        Assert.Null(RevocationFetchPolicy.ReadBounded(big, 50, CancellationToken.None));

        var small = new ByteArrayContent(new byte[40]);
        Assert.Equal(40, RevocationFetchPolicy.ReadBounded(small, 50, CancellationToken.None)!.Length);
    }

    [Fact]
    public void A_streamed_answer_without_content_length_is_cut_at_the_cap()
    {
        var content = new StreamContent(new MemoryStream(new byte[200_000]));
        Assert.Null(RevocationFetchPolicy.ReadBounded(content, RevocationFetchPolicy.OcspMaxBytes, CancellationToken.None));
    }

    [Fact]
    public void A_crl_that_cannot_be_read_is_cached_for_the_default()
    {
        Assert.Equal(RevocationFetchPolicy.UnknownTtl,
            RevocationFetchPolicy.TtlFor(new byte[] { 1, 2, 3 }, DateTime.UtcNow));
    }
}
