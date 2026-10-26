using DotNetSigningServer.Services.Consents;

namespace DotNetSigningServer.Tests.Services.Consents;

public class UserAgentCoarseningTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.6099.109 Safari/537.36", "Chrome 120 / Windows")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/119.0.2151.58", "Edge 119 / Windows")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Safari/605.1.15", "Safari 17 / macOS")]
    [InlineData("Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0", "Firefox 121 / Linux")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/119.0.6045.109 Mobile/15E148 Safari/604.1", "Chrome 119 / iOS")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36 OPR/79.0.4143.72", "Opera 79 / Android")]
    public void KeepsFamilyMajorVersionAndOs(string ua, string expected) =>
        Assert.Equal(expected, UserAgentCoarsening.Coarsen(ua));

    [Fact]
    public void DropsDeviceModelAndMinorVersions()
    {
        var result = UserAgentCoarsening.Coarsen("Mozilla/5.0 (Linux; Android 14; Pixel 8 Build/UP1A.231105.001) Chrome/120.0.6099.144 Mobile Safari/537.36");
        Assert.DoesNotContain("Pixel", result);
        Assert.DoesNotContain("6099", result);
    }

    [Fact]
    public void OnlyOs_WhenBrowserUnknown() =>
        Assert.Equal("Windows", UserAgentCoarsening.Coarsen("SomeClient (Windows NT 10.0)"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("curl/8.4.0")]
    [InlineData("Mozilla/5.0 (test)")]
    public void Unrecognised_ReturnsNull(string? ua) =>
        Assert.Null(UserAgentCoarsening.Coarsen(ua));
}
