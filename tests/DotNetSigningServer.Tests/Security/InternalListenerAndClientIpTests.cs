using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Middleware;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Operations;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DotNetSigningServer.Tests.Security;

/// <summary>
/// #78: the portal's token only on the internal listener, its tenant headers and the billing
/// bypass only from there. #106: CF-Connecting-IP only from a configured proxy.
/// </summary>
/// <summary>
/// #131: the provisioning token only on the internal listener when there is one; every other token
/// on any listener. #106: CF-Connecting-IP only from a configured proxy.
/// </summary>
[Collection("ApiGate")]
public class InternalListenerAndClientIpTests
{
    private const int PublicPort = 8085;
    private const int InternalPort = 8086;

    private static InternalListenerOptions Listener(int? internalPort = InternalPort) => new() { InternalPort = internalPort };

    // ── Authentication handler: which port a token works on ─────────────────────────────────────

    private static async Task<(AuthenticateResult Result, DefaultHttpContext Context)> AuthenticateAsync(
        InternalListenerOptions listener, int localPort, bool provisioning = false, bool browserToken = false, User? user = null)
    {
        user ??= new User { Id = Guid.NewGuid(), Email = "u@example.test", IsActive = true };
        var apiAuth = new Mock<IApiAuthService>();
        apiAuth.Setup(a => a.ValidateTokenDetailedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IPAddress?>()))
            .ReturnsAsync(new ApiTokenValidation(user, Guid.NewGuid(), browserToken, provisioning));
        var monitor = new Mock<IOptionsMonitor<AuthenticationSchemeOptions>>();
        monitor.Setup(m => m.Get(It.IsAny<string>())).Returns(new AuthenticationSchemeOptions());

        var handler = new ApiKeyAuthenticationHandler(monitor.Object, NullLoggerFactory.Instance, UrlEncoder.Default, apiAuth.Object, listener);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/provisioning/accounts";
        context.Request.Headers.Authorization = "Bearer some-token";
        context.Connection.LocalPort = localPort;
        await handler.InitializeAsync(new AuthenticationScheme(ApiKeyAuthenticationHandler.SchemeName, null, typeof(ApiKeyAuthenticationHandler)), context);
        return (await handler.AuthenticateAsync(), context);
    }

    [Fact]
    public async Task Provisioning_token_on_the_public_port_is_not_authenticated()
    {
        var (result, _) = await AuthenticateAsync(Listener(), PublicPort, provisioning: true);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Provisioning_token_on_the_internal_port_carries_its_kind()
    {
        var (result, _) = await AuthenticateAsync(Listener(), InternalPort, provisioning: true);
        Assert.True(result.Succeeded);
        Assert.Equal(SessionClaims.ProvisioningTokenKind, result.Principal!.FindFirst(SessionClaims.TokenKind)?.Value);
    }

    [Fact]
    public async Task Without_an_internal_port_the_provisioning_token_works_on_the_one_listener()
    {
        var (result, _) = await AuthenticateAsync(Listener(internalPort: null), PublicPort, provisioning: true);
        Assert.True(result.Succeeded);
        Assert.True(SessionClaims.IsProvisioningToken(result.Principal));
    }

    [Theory]
    [InlineData(PublicPort)]
    [InlineData(InternalPort)]
    public async Task Other_tokens_work_on_either_port(int port)
    {
        var (server, _) = await AuthenticateAsync(Listener(), port);
        Assert.True(server.Succeeded);
        Assert.Equal(SessionClaims.ServerTokenKind, server.Principal!.FindFirst(SessionClaims.TokenKind)?.Value);
        var (browser, _) = await AuthenticateAsync(Listener(), port, browserToken: true);
        Assert.True(browser.Succeeded);
        Assert.True(SessionClaims.IsBrowserToken(browser.Principal));
    }

    [Fact]
    public async Task A_provisioned_account_carries_its_external_id_for_the_log_scope()
    {
        var parent = new User { Id = Guid.NewGuid(), Email = "p@service.invalid", IsActive = true, IsEnterprise = true };
        var child = new User { Id = Guid.NewGuid(), Email = "c@service.invalid", IsActive = true, ParentUserId = parent.Id, Parent = parent, ExternalId = "tenant-a" };
        var (result, _) = await AuthenticateAsync(Listener(), PublicPort, user: child);
        Assert.Equal("tenant-a", result.Principal!.FindFirst(SessionClaims.AccountRef)?.Value);
        var (plain, _) = await AuthenticateAsync(Listener(), PublicPort);
        Assert.Null(plain.Principal!.FindFirst(SessionClaims.AccountRef));
    }

    [Fact]
    public async Task Provisioning_token_from_the_public_hostname_gets_401_before_the_body()
    {
        var (result, context) = await AuthenticateAsync(Listener(), PublicPort, provisioning: true);
        if (result.Succeeded) context.User = result.Principal!;

        var services = new ServiceCollection();
        services.AddSingleton<IStringLocalizerFactory>(new KeyEchoLocalizerFactory());
        context.RequestServices = services.BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        var reached = false;
        var gate = new ApiGateMiddleware(_ => { reached = true; return Task.CompletedTask; },
            Microsoft.Extensions.Options.Options.Create(new RateLimitOptions()));

        await gate.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(reached);
    }

    // ── Configuration ───────────────────────────────────────────────────────────────────────────

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    [Fact]
    public void Internal_port_is_read()
    {
        Assert.Equal(8086, InternalListenerOptions.From(Config(("INTERNAL_API_PORT", "8086"))).InternalPort);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{{project.DOTNET_INTERNAL_API_PORT}}")]
    public void Unset_and_placeholder_values_mean_no_internal_listener(string? value)
    {
        Assert.Null(InternalListenerOptions.From(Config(("INTERNAL_API_PORT", value))).InternalPort);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("port")]
    public void A_port_that_is_not_a_port_stops_the_start(string value)
    {
        Assert.Throws<InvalidOperationException>(() => InternalListenerOptions.From(Config(("INTERNAL_API_PORT", value))));
    }

    [Theory]
    [InlineData("http://+:8085", "http://+:8085;http://+:8086")]
    [InlineData("http://+:8085;http://+:8086", "http://+:8085;http://+:8086")]
    [InlineData(null, "http://localhost:5000;http://+:8086")]
    public void The_internal_listener_is_added_next_to_the_public_one(string? urls, string expected)
    {
        Assert.Equal(expected, InternalListenerOptions.WithInternalListener(urls, InternalPort));
    }

    // ── Correlation in the log ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Loki_lines_carry_the_account_and_request_id()
    {
        var scopes = new LoggerExternalScopeProvider();
        using (scopes.Push(new Dictionary<string, object?>
        {
            [RequestCorrelationMiddleware.AccountScopeKey] = "tenant-a",
            [RequestCorrelationMiddleware.RequestIdScopeKey] = "req-1",
        }))
        {
            Assert.Equal(" [account=tenant-a requestId=req-1]", DotNetSigningServer.Logging.LokiLogger.CorrelationSuffix(scopes));
        }
        Assert.Null(DotNetSigningServer.Logging.LokiLogger.CorrelationSuffix(scopes));
    }

    [Theory]
    [InlineData("req-1", "req-1")]
    [InlineData("a\nb", null)]
    [InlineData("", null)]
    public void Request_id_outside_the_safe_alphabet_is_dropped(string raw, string? expected)
    {
        Assert.Equal(expected, RequestCorrelationMiddleware.Clean(raw, RequestCorrelationMiddleware.MaxRequestIdLength));
        Assert.Null(RequestCorrelationMiddleware.Clean(new string('x', 200), RequestCorrelationMiddleware.MaxRequestIdLength));
    }

    [Fact]
    public void No_tenant_header_is_read_anywhere()
    {
        var root = RepoRoot();
        var offenders = new[] { "Controllers", "Services", "Middleware" }
            .SelectMany(dir => Directory.GetFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            .Where(f => File.ReadAllText(f).Contains("X-P4-Tenant", StringComparison.Ordinal)
                        || File.ReadAllText(f).Contains("Attachment-Billing-Bypass", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    // ── Client IP behind Cloudflare (#78 A4, #106) ──────────────────────────────────────────────

    // cloudflared at 172.18.0.5 or in 10.0.1.0/24; Traefik at 172.18.0.2.
    private static readonly ClientIpTrust Tunnel = new(
        TrustedProxies.Parse("172.18.0.5", "10.0.1.0/24"), TrustedProxies.Parse("172.18.0.2"));

    private static DefaultHttpContext FromPeer(string peer, string? cf)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (cf != null) context.Request.Headers["CF-Connecting-IP"] = cf;
        return context;
    }

    [Fact]
    public void Behind_Cloudflare_the_client_is_CF_Connecting_IP_not_the_tunnel()
    {
        Assert.Equal(IPAddress.Parse("203.0.113.7"), ClientIp.AddressOf(FromPeer("172.18.0.5", "203.0.113.7"), Tunnel));
        Assert.Equal(IPAddress.Parse("203.0.113.8"), ClientIp.AddressOf(FromPeer("10.0.1.9", "203.0.113.8"), Tunnel));
    }

    [Fact]
    public void CF_Connecting_IP_from_a_private_peer_that_is_not_a_trusted_proxy_is_ignored()
    {
        // Caddy, another container, anything in the Docker network that is not the tunnel.
        Assert.Equal(IPAddress.Parse("172.18.0.99"), ClientIp.AddressOf(FromPeer("172.18.0.99", "203.0.113.7"), Tunnel));
    }

    [Fact]
    public void A_reverse_proxy_alone_does_not_vouch_for_CF_Connecting_IP()
    {
        // Traefik forwards whatever reached it, tunnel or not: from it, the header counts only when
        // the request it forwards came from cloudflared.
        var context = FromPeer("172.18.0.2", "203.0.113.7");
        Assert.Equal(IPAddress.Parse("172.18.0.2"), ClientIp.AddressOf(context, Tunnel));
    }

    [Fact]
    public async Task Through_the_reverse_proxy_a_request_from_cloudflared_is_believed()
    {
        var context = FromPeer("172.18.0.2", "203.0.113.7");
        await ClientIp.CaptureSocketPeer(context, () =>
        {
            // UseForwardedHeaders applied Traefik's X-Forwarded-For: the request came from cloudflared.
            context.Connection.RemoteIpAddress = IPAddress.Parse("172.18.0.5");
            return Task.CompletedTask;
        });
        Assert.Equal(IPAddress.Parse("203.0.113.7"), ClientIp.AddressOf(context, Tunnel));
    }

    [Fact]
    public async Task Through_the_reverse_proxy_a_request_from_elsewhere_is_not_believed()
    {
        var context = FromPeer("172.18.0.2", "203.0.113.7");
        await ClientIp.CaptureSocketPeer(context, () =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.1");
            return Task.CompletedTask;
        });
        Assert.Equal(IPAddress.Parse("198.51.100.1"), ClientIp.AddressOf(context, Tunnel));
    }

    [Fact]
    public void Empty_TRUSTED_CF_PROXIES_ignores_the_header()
    {
        var trust = ClientIpTrust.From(Config(("TRUSTED_CF_PROXIES", ""), ("TRUSTED_PROXIES", "172.18.0.5")));
        Assert.Equal(IPAddress.Parse("172.18.0.5"), ClientIp.AddressOf(FromPeer("172.18.0.5", "203.0.113.7"), trust));
    }

    private static Microsoft.AspNetCore.Builder.ForwardedHeadersOptions Forwarded(string? proxies, string? networks = null, string? known = null)
    {
        var options = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions();
        ClientIpTrust.ApplyTo(options, TrustedProxies.Parse(proxies), networks, known);
        return options;
    }

    [Fact]
    public void TRUSTED_PROXIES_none_trusts_no_X_Forwarded_For_at_all()
    {
        var options = Forwarded("none");
        Assert.Empty(options.KnownNetworks);
        Assert.Empty(options.KnownProxies);
    }

    [Fact]
    public void TRUSTED_PROXIES_is_what_X_Forwarded_For_is_accepted_from()
    {
        var options = Forwarded("172.18.0.2, 10.9.0.0/16");
        Assert.Equal(new[] { IPAddress.Parse("172.18.0.2") }, options.KnownProxies);
        Assert.Single(options.KnownNetworks);
    }

    [Fact]
    public void Unset_TRUSTED_PROXIES_keeps_the_private_range_default()
    {
        var options = Forwarded(null);
        Assert.Contains(options.KnownNetworks, n => n.Prefix.Equals(IPAddress.Parse("172.16.0.0")) && n.PrefixLength == 12);
    }

    [Fact]
    public void An_explicit_ForwardedHeaders_list_wins()
    {
        var options = Forwarded("none", networks: "10.1.0.0/16", known: "172.18.0.9");
        Assert.Equal(new[] { IPAddress.Parse("172.18.0.9") }, options.KnownProxies);
        Assert.Single(options.KnownNetworks);
    }

    [Fact]
    public void Without_trusted_proxies_CF_Connecting_IP_is_never_believed()
    {
        var context = FromPeer("172.18.0.5", "203.0.113.7");
        context.RequestServices = new ServiceCollection().BuildServiceProvider();
        Assert.Equal("172.18.0.5", ClientIp.Of(context));
    }

    [Fact]
    public void A_spoofed_X_Original_For_does_not_make_a_peer_trusted()
    {
        var context = FromPeer("93.184.216.34", "203.0.113.7");
        context.Request.Headers["X-Original-For"] = "172.18.0.5";
        Assert.Equal(IPAddress.Parse("93.184.216.34"), ClientIp.AddressOf(context, Tunnel));
    }

    [Fact]
    public async Task The_socket_peer_is_the_one_before_forwarded_headers_rewrote_it()
    {
        var context = FromPeer("172.18.0.5", "203.0.113.7");
        await ClientIp.CaptureSocketPeer(context, () =>
        {
            // What UseForwardedHeaders does with X-Forwarded-For.
            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.1");
            return Task.CompletedTask;
        });
        Assert.Equal(IPAddress.Parse("203.0.113.7"), ClientIp.AddressOf(context, Tunnel));
    }

    [Fact]
    public void An_IPv4_mapped_peer_matches_an_IPv4_proxy_entry()
    {
        Assert.Equal(IPAddress.Parse("203.0.113.7"), ClientIp.AddressOf(FromPeer("::ffff:172.18.0.5", "203.0.113.7"), Tunnel));
    }

    [Fact]
    public void Trusted_proxies_that_do_not_parse_fail_startup()
    {
        Assert.Throws<InvalidOperationException>(() => TrustedProxies.Parse("not-an-ip"));
        Assert.Throws<InvalidOperationException>(() => TrustedProxies.Parse("10.0.0.0/40"));
    }

    [Fact]
    public void None_declares_no_proxy_on_purpose()
    {
        var none = TrustedProxies.Parse("none");
        Assert.True(none.IsEmpty);
        Assert.True(none.DeclaredNone);
        Assert.False(TrustedProxies.Parse("").DeclaredNone);
    }

    /// <summary>
    /// One helper for every client address (#78): the consent evidence (AccountController), the
    /// token IP whitelist (authentication handler, controller fallback) and the rate limits.
    /// A direct read of the peer address elsewhere is the proxy's address behind Cloudflare.
    /// </summary>
    [Fact]
    public void Client_address_is_read_through_ClientIp_only()
    {
        var root = RepoRoot();
        var offenders = new[] { "Controllers", "Services", "Middleware", "Filters" }
            .SelectMany(dir => Directory.GetFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            .Where(f => Path.GetFileName(f) != "ClientIp.cs")
            .Where(f => File.ReadAllText(f).Contains("Connection.RemoteIpAddress", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Program.cs"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
