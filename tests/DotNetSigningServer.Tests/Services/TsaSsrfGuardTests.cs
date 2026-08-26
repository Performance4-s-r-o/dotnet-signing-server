using System.Net;
using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Services;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// The TSA URL on presign/sign/timestamp/seal/tsa-probe is caller-controlled, so it
/// is a direct outbound-request primitive into whatever network the server sits on.
/// </summary>
public class TsaSsrfGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // cloud instance metadata
    [InlineData("100.64.0.1")]      // CGNAT
    [InlineData("0.0.0.0")]
    [InlineData("fd00::1")]         // unique-local
    [InlineData("::ffff:10.0.0.1")] // IPv4-mapped private
    public void IsPrivateOrLocalAddress_InternalRanges_ReturnsTrue(string address)
    {
        Assert.True(PdfCryptoHelper.IsPrivateOrLocalAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]  // just outside 172.16.0.0/12
    [InlineData("172.15.255.1")]
    [InlineData("100.128.0.1")] // just outside 100.64.0.0/10
    [InlineData("2001:4860:4860::8888")]
    public void IsPrivateOrLocalAddress_PublicAddresses_ReturnsFalse(string address)
    {
        Assert.False(PdfCryptoHelper.IsPrivateOrLocalAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("http://tsa.example.com/tsr")]
    [InlineData("ftp://tsa.example.com/tsr")]
    [InlineData("file:///etc/passwd")]
    public void CreateTsaClient_CallerSuppliedNonHttpsUrl_Throws(string url)
    {
        var ex = Assert.Throws<ApiValidationException>(() =>
            PdfCryptoHelper.CreateTsaClient(url, null, null));
        Assert.Equal("TSA_HTTPS_REQUIRED", ex.Code);
    }

    [Theory]
    [InlineData("https://127.0.0.1/tsr")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://10.0.0.5/tsr")]
    [InlineData("https://192.168.0.1/tsr")]
    public void CreateTsaClient_CallerSuppliedInternalHost_Throws(string url)
    {
        var ex = Assert.Throws<ApiValidationException>(() =>
            PdfCryptoHelper.CreateTsaClient(url, null, null));
        Assert.Equal("TSA_HOST_NOT_ALLOWED", ex.Code);
    }

    [Fact]
    public void CreateTsaClient_UnresolvableHost_FailsClosed()
    {
        var ex = Assert.Throws<ApiValidationException>(() =>
            PdfCryptoHelper.CreateTsaClient(
                "https://no-such-host.invalid/tsr",
                null, null));
        Assert.Equal("TSA_HOST_NOT_ALLOWED", ex.Code);
    }

    [Fact]
    public void CreateTsaClient_CallerSuppliedPublicHost_UsesConnectTimePinnedClient()
    {
        // The validation-time DNS lookup and the client's own lookup are separate
        // resolutions, so a zero-TTL attacker domain could answer public then private.
        // Caller-supplied URLs must go through the client that re-checks at connect time.
        // IP literal so the guard resolves without a DNS round-trip — keeps the test offline.
        var client = PdfCryptoHelper.CreateTsaClient("https://8.8.8.8/tsr", null, null);

        Assert.IsType<PinnedIpTsaClient>(client);
    }

    [Fact]
    public void CreateTsaClient_NoUrl_ReturnsNull()
    {
        // Timestamping is the caller's decision, and this server has no TSA of its
        // own to fall back on — no URL in the request means no timestamp, so a
        // document is never stamped by an authority the caller didn't name.
        var client = PdfCryptoHelper.CreateTsaClient(null, null, null);

        Assert.Null(client);
    }
}
