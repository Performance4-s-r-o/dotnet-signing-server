using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace DotNetSigningServer.Middleware;

/// <summary>A list of addresses and CIDRs from configuration (#106), or <c>none</c>.</summary>
public sealed class TrustedProxies
{
    /// <summary>Reverse proxies (Traefik, Caddy): whose <c>X-Forwarded-For</c> is believed.</summary>
    public const string Variable = "TRUSTED_PROXIES";

    /// <summary>cloudflared: whose <c>CF-Connecting-IP</c> is believed.</summary>
    public const string CloudflareVariable = "TRUSTED_CF_PROXIES";

    public static readonly TrustedProxies None = new(Array.Empty<IPAddress>(), Array.Empty<IPNetwork>());

    private readonly IPAddress[] _addresses;
    private readonly IPNetwork[] _networks;

    public TrustedProxies(IEnumerable<IPAddress> addresses, IEnumerable<IPNetwork> networks)
    {
        _addresses = addresses.Select(Normalize).ToArray();
        _networks = networks.ToArray();
    }

    public IReadOnlyList<IPAddress> Addresses => _addresses;
    public IReadOnlyList<IPNetwork> Networks => _networks;

    public bool IsEmpty => _addresses.Length == 0 && _networks.Length == 0;

    /// <summary><c>none</c>: no such proxy in front of this server, said on purpose.</summary>
    public bool DeclaredNone { get; private init; }

    public bool Contains(IPAddress? address)
    {
        if (address is null) return false;
        var ip = Normalize(address);
        return _addresses.Any(a => a.Equals(ip)) || _networks.Any(n => n.Contains(ip));
    }

    /// <summary>Addresses and CIDRs separated by commas, semicolons or whitespace. Anything else fails startup.</summary>
    public static TrustedProxies Parse(params string?[] lists) => ParseNamed(Variable, lists);

    public static TrustedProxies ParseNamed(string name, params string?[] lists)
    {
        var addresses = new List<IPAddress>();
        var networks = new List<IPNetwork>();
        var none = false;
        foreach (var list in lists)
        {
            if (Options.EnvValues.IsUnset(list)) continue;
            foreach (var entry in list!.Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(entry.Trim(), "none", StringComparison.OrdinalIgnoreCase))
                {
                    none = true;
                    continue;
                }
                var parts = entry.Trim().Split('/', 2);
                if (!IPAddress.TryParse(parts[0], out var address))
                {
                    throw new InvalidOperationException($"{name}: '{entry}' is not an IP address or CIDR.");
                }
                if (parts.Length == 1)
                {
                    addresses.Add(address);
                }
                else if (int.TryParse(parts[1], out var length) && length >= 0
                         && length <= (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
                {
                    networks.Add(new IPNetwork(address, length));
                }
                else
                {
                    throw new InvalidOperationException($"{name}: '{entry}' is not an IP address or CIDR.");
                }
            }
        }
        return new TrustedProxies(addresses, networks) { DeclaredNone = none };
    }

    internal static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}

/// <summary>
/// Who may name the client (#106), two separate lists: <see cref="Cloudflare"/> (cloudflared,
/// <c>TRUSTED_CF_PROXIES</c>) for <c>CF-Connecting-IP</c>, <see cref="Proxies"/> (the reverse
/// proxy, <c>TRUSTED_PROXIES</c> plus an explicit <c>ForwardedHeaders:*</c>) for <c>X-Forwarded-For</c>.
/// A reverse proxy is not allowed to vouch for <c>CF-Connecting-IP</c> on its own: anything that
/// reaches it outside the tunnel could write that header.
/// </summary>
public sealed record ClientIpTrust(TrustedProxies Cloudflare, TrustedProxies Proxies)
{
    public static readonly ClientIpTrust None = new(TrustedProxies.None, TrustedProxies.None);

    public static ClientIpTrust From(IConfiguration configuration) => new(
        TrustedProxies.ParseNamed(TrustedProxies.CloudflareVariable, configuration[TrustedProxies.CloudflareVariable]),
        TrustedProxies.ParseNamed(TrustedProxies.Variable,
            configuration[TrustedProxies.Variable],
            configuration["ForwardedHeaders:KnownProxies"],
            configuration["ForwardedHeaders:KnownNetworks"]));

    /// <summary>
    /// Whose <c>X-Forwarded-For</c> the forwarded-headers middleware applies. An explicit
    /// <c>ForwardedHeaders:*</c> list wins; then <c>TRUSTED_PROXIES</c>; <c>none</c> = nobody's;
    /// unset = the private ranges and loopback (the behaviour before #106).
    /// </summary>
    public static void ApplyTo(ForwardedHeadersOptions options, TrustedProxies proxies, string? configuredNetworks, string? configuredProxies)
    {
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();

        if (!Options.EnvValues.IsUnset(configuredNetworks) || !Options.EnvValues.IsUnset(configuredProxies))
        {
            var explicitList = TrustedProxies.ParseNamed("ForwardedHeaders", configuredNetworks, configuredProxies);
            foreach (var address in explicitList.Addresses) options.KnownProxies.Add(address);
            foreach (var network in explicitList.Networks) options.KnownNetworks.Add(network);
            return;
        }
        if (!proxies.IsEmpty)
        {
            foreach (var address in proxies.Addresses) options.KnownProxies.Add(address);
            foreach (var network in proxies.Networks) options.KnownNetworks.Add(network);
            return;
        }
        if (proxies.DeclaredNone)
        {
            // No proxy in front: X-Forwarded-For is just a header somebody wrote.
            return;
        }

        // Default: Docker/Kubernetes private ranges + loopback.
        options.KnownNetworks.Add(new IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
        options.KnownNetworks.Add(new IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
        options.KnownNetworks.Add(new IPNetwork(IPAddress.Parse("192.168.0.0"), 16));
        options.KnownNetworks.Add(new IPNetwork(IPAddress.Parse("127.0.0.0"), 8));
        options.KnownNetworks.Add(new IPNetwork(IPAddress.Parse("::1"), 128));
        options.KnownNetworks.Add(new IPNetwork(IPAddress.Parse("fc00::"), 7));
    }
}

/// <summary>
/// The client's address — the one helper for rate limits, the token IP whitelist and the IP
/// in consent evidence (#78, #106). <c>CF-Connecting-IP</c> is the client only when the request
/// came from cloudflared (<see cref="ClientIpTrust.Cloudflare"/>): either cloudflared is the TCP
/// peer, or the TCP peer is a trusted reverse proxy that forwarded a request it got from
/// cloudflared. From anyone else, private networks included, it is just a header somebody wrote,
/// and the address is the one after the forwarded-headers middleware. A value that does not
/// parse is ignored.
/// </summary>
public static class ClientIp
{
    /// <summary>HttpContext.Items key: the TCP peer, captured before UseForwardedHeaders rewrites it.</summary>
    public const string SocketPeerItemKey = "p4:socket-peer";

    /// <summary>Runs before <c>UseForwardedHeaders</c>: remembers who actually opened the connection.</summary>
    public static Task CaptureSocketPeer(HttpContext context, Func<Task> next)
    {
        context.Items[SocketPeerItemKey] = context.Connection.RemoteIpAddress;
        return next();
    }

    public static string Of(HttpContext context) => AddressOf(context)?.ToString() ?? "unknown";

    public static IPAddress? AddressOf(HttpContext context)
    {
        var trust = context.RequestServices?.GetService(typeof(ClientIpTrust)) as ClientIpTrust ?? ClientIpTrust.None;
        return AddressOf(context, trust);
    }

    public static IPAddress? AddressOf(HttpContext context, ClientIpTrust trust)
    {
        // After the forwarded-headers middleware: X-Forwarded-For of a trusted reverse proxy.
        var peer = context.Connection.RemoteIpAddress;
        // Never X-Original-For: when the forwarded-headers middleware does not apply, that header
        // is still whatever the client sent.
        var socketPeer = context.Items.TryGetValue(SocketPeerItemKey, out var captured) && captured is IPAddress socket
            ? socket
            : peer;

        var fromCloudflared = trust.Cloudflare.Contains(socketPeer)
            || (trust.Proxies.Contains(socketPeer) && trust.Cloudflare.Contains(peer));

        var header = context.Request.Headers["CF-Connecting-IP"].ToString();
        if (fromCloudflared
            && !string.IsNullOrWhiteSpace(header)
            && IPAddress.TryParse(header.Trim(), out var cf))
        {
            return TrustedProxies.Normalize(cf);
        }
        return peer is null ? null : TrustedProxies.Normalize(peer);
    }
}
