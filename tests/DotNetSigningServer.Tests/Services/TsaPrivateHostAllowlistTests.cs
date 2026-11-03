using System.Net;
using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Services;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// TSA_PRIVATE_HOST_ALLOWLIST: an exact operator-set name may resolve to a private range,
/// never to loopback, link-local or metadata; empty keeps the public-only rule.
/// </summary>
[Collection("TsaStatics")]
public sealed class TsaPrivateHostAllowlistTests : IDisposable
{
    private const string Allowed = "tsa.ops.example.test";
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _savedResolver = PinnedIpHttp.Resolver;
    private readonly Dictionary<string, IPAddress[]> _dns = new(StringComparer.OrdinalIgnoreCase);

    public TsaPrivateHostAllowlistTests()
    {
        var real = _savedResolver;
        // Only the names below are faked; any other lookup (tests running alongside) goes to real DNS.
        PinnedIpHttp.Resolver = (host, ct) =>
            _dns.TryGetValue(host, out var a) ? Task.FromResult(a) : real(host, ct);
    }

    public void Dispose()
    {
        PinnedIpHttp.Resolver = _savedResolver;
        TsaPrivateHosts.Configure(null);
    }

    private void Resolve(string host, params string[] addresses) =>
        _dns[host] = addresses.Select(IPAddress.Parse).ToArray();

    private static string? Refusal(string url)
    {
        try
        {
            PdfCryptoHelper.CreateTsaClient(url, null, null);
            return null;
        }
        catch (ApiValidationException ex)
        {
            return ex.Code;
        }
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.20.1.2")]
    [InlineData("192.168.10.4")]
    [InlineData("100.64.0.9")]
    [InlineData("fd12::5")]
    [InlineData("::ffff:10.1.2.3")]
    public async Task AllowlistedHost_OnPrivateAddress_IsAccepted_AndPinned(string address)
    {
        TsaPrivateHosts.Configure($"other.example.test, {Allowed.ToUpperInvariant()}.");
        Resolve(Allowed, address);

        Assert.Null(Refusal($"https://{Allowed}/tsr"));
        var pinned = await PinnedIpHttp.ResolvePublicAddressAsync(
            Allowed, "TSA_HOST_NOT_ALLOWED", TsaPrivateHosts.IsAcceptable, CancellationToken.None);
        Assert.Equal(IPAddress.Parse(address), pinned);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("169.254.169.254")]
    [InlineData("fe80::1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("::ffff:169.254.169.254")]
    public async Task AllowlistedHost_OnLoopbackLinkLocalOrMetadata_IsRefused(string address)
    {
        TsaPrivateHosts.Configure(Allowed);
        Resolve(Allowed, address);

        Assert.Equal("TSA_HOST_NOT_ALLOWED", Refusal($"https://{Allowed}/tsr"));
        var ex = await Assert.ThrowsAsync<ApiValidationException>(() => PinnedIpHttp.ResolvePublicAddressAsync(
            Allowed, "TSA_HOST_NOT_ALLOWED", TsaPrivateHosts.IsAcceptable, CancellationToken.None));
        Assert.Equal("TSA_HOST_NOT_ALLOWED", ex.Code);
    }

    [Fact]
    public void AllowlistedHost_WithOneForbiddenAddress_IsRefused()
    {
        TsaPrivateHosts.Configure(Allowed);
        Resolve(Allowed, "10.0.0.5", "169.254.169.254");

        Assert.Equal("TSA_HOST_NOT_ALLOWED", Refusal($"https://{Allowed}/tsr"));
    }

    [Fact]
    public void OtherHosts_StayRefused_ExactMatchOnly()
    {
        TsaPrivateHosts.Configure(Allowed);
        foreach (var host in new[] { "evil.example.test", $"x.{Allowed}", $"{Allowed}.evil.test" })
        {
            Resolve(host, "10.0.0.5");
            Assert.Equal("TSA_HOST_NOT_ALLOWED", Refusal($"https://{host}/tsr"));
        }
        // An IP literal is never on the allowlist.
        Assert.Equal("TSA_HOST_NOT_ALLOWED", Refusal("https://10.0.0.5/tsr"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{{project.DOTNET_TSA_PRIVATE_HOST_ALLOWLIST}}")]
    public void EmptyAllowlist_KeepsPublicOnlyRule(string? value)
    {
        TsaPrivateHosts.Configure(value);
        Resolve(Allowed, "10.0.0.5");

        Assert.Equal("TSA_HOST_NOT_ALLOWED", Refusal($"https://{Allowed}/tsr"));
        Assert.False(TsaPrivateHosts.IsAcceptable(Allowed, IPAddress.Parse("10.0.0.5")));
        Assert.True(TsaPrivateHosts.IsAcceptable(Allowed, IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void AllowlistedHost_StillNeedsHttps()
    {
        TsaPrivateHosts.Configure(Allowed);
        Resolve(Allowed, "10.0.0.5");

        Assert.Equal("TSA_HTTPS_REQUIRED", Refusal($"http://{Allowed}/tsr"));
    }

    [Fact]
    public void RevocationGuard_IgnoresTheAllowlist()
    {
        TsaPrivateHosts.Configure(Allowed);
        Resolve(Allowed, "10.0.0.5");

        Assert.True(PdfCryptoHelper.ResolvesToPrivateAddress(Allowed));
    }

    [Theory]
    [InlineData("*.example.test")]
    [InlineData("10.0.0.5")]
    [InlineData("::1")]
    [InlineData("localhost")]
    [InlineData("tsa")]
    [InlineData("a.localhost")]
    [InlineData("tsa.example.test/x")]
    [InlineData("1.2.3.4.5")]
    public void InvalidEntry_StopsTheStart(string entry)
    {
        Assert.Throws<InvalidOperationException>(() => TsaPrivateHosts.Configure($"ok.example.test,{entry}"));
    }
}
