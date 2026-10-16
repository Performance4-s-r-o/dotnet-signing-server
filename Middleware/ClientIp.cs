using System.Net;
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace DotNetSigningServer.Middleware;

/// <summary>
/// The proxies whose <c>CF-Connecting-IP</c> is believed (#106): cloudflared and the reverse
/// proxy in front of this server. Built once from <c>TRUSTED_PROXIES</c> plus an explicit
/// <c>ForwardedHeaders:KnownProxies</c> / <c>KnownNetworks</c>; never from the private-range default.
/// </summary>
public sealed class TrustedProxies
{
    public const string Variable = "TRUSTED_PROXIES";

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

    /// <summary><c>none</c>: no proxy in front of this server, said on purpose (an on-prem host without a tunnel).</summary>
    public bool DeclaredNone { get; private init; }

    public bool Contains(IPAddress? address)
    {
        if (address is null) return false;
        var ip = Normalize(address);
        return _addresses.Any(a => a.Equals(ip)) || _networks.Any(n => n.Contains(ip));
    }

    /// <summary>Addresses and CIDRs separated by commas, semicolons or whitespace. Anything else fails startup.</summary>
    public static TrustedProxies Parse(params string?[] lists)
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
                    throw new InvalidOperationException($"{Variable}: '{entry}' is not an IP address or CIDR.");
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
                    throw new InvalidOperationException($"{Variable}: '{entry}' is not an IP address or CIDR.");
                }
            }
        }
        return new TrustedProxies(addresses, networks) { DeclaredNone = none };
    }

    internal static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}

/// <summary>
/// The client's address — the one helper for rate limits, the token IP whitelist and the IP
/// in consent evidence (#78, #106). The origin is reached through the Cloudflare tunnel, so
/// <c>CF-Connecting-IP</c> is the client — but only when the TCP peer is a configured trusted
/// proxy (<see cref="TrustedProxies"/>). From any other peer, private networks included, it is
/// just a header somebody wrote, and the address is the one after the forwarded-headers
/// middleware. A value that does not parse is ignored.
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
        var trusted = context.RequestServices?.GetService(typeof(TrustedProxies)) as TrustedProxies ?? TrustedProxies.None;
        return AddressOf(context, trusted);
    }

    public static IPAddress? AddressOf(HttpContext context, TrustedProxies trusted)
    {
        var peer = context.Connection.RemoteIpAddress;
        // Never X-Original-For: when the forwarded-headers middleware does not apply, that header
        // is still whatever the client sent.
        var socketPeer = context.Items.TryGetValue(SocketPeerItemKey, out var captured) && captured is IPAddress socket
            ? socket
            : peer;

        var header = context.Request.Headers["CF-Connecting-IP"].ToString();
        if (trusted.Contains(socketPeer)
            && !string.IsNullOrWhiteSpace(header)
            && IPAddress.TryParse(header.Trim(), out var cf))
        {
            return TrustedProxies.Normalize(cf);
        }
        return peer is null ? null : TrustedProxies.Normalize(peer);
    }
}
