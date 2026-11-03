using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using DotNetSigningServer.Options;

namespace DotNetSigningServer.Services;

/// <summary>
/// <c>TSA_PRIVATE_HOST_ALLOWLIST</c>: TSA host names the operator lets resolve to a private
/// address (RFC 1918, CGNAT, IPv6 unique-local). Exact names only, set from the environment.
///
/// Loopback, link-local (cloud metadata), multicast and the reserved ranges stay refused for
/// these names too, and the address is still pinned at connect time. Empty = no exception.
/// </summary>
public static class TsaPrivateHosts
{
    private static readonly Regex Label = new("^(?!-)[a-z0-9-]{1,63}(?<!-)$", RegexOptions.CultureInvariant);

    private static volatile HashSet<string> _hosts = new(StringComparer.Ordinal);

    /// <summary>Replaces the allowlist; startup and tests. An invalid entry throws, the start fails.</summary>
    public static void Configure(string? value)
    {
        var hosts = new HashSet<string>(StringComparer.Ordinal);
        if (!EnvValues.IsUnset(value))
        {
            foreach (var raw in value!.Split(','))
            {
                var name = Normalize(raw);
                if (name.Length == 0) continue;
                if (!IsValidName(name))
                {
                    throw new InvalidOperationException(
                        $"TSA_PRIVATE_HOST_ALLOWLIST: '{raw.Trim()}' is not an exact DNS name (no wildcard, no IP address).");
                }
                hosts.Add(name);
            }
        }
        _hosts = hosts;
    }

    public static bool IsAllowed(string host) => _hosts.Count > 0 && _hosts.Contains(Normalize(host));

    /// <summary>
    /// The private ranges an allowlisted name may resolve to: 10/8, 172.16/12, 192.168/16,
    /// 100.64/10 and fc00::/7. Nothing else.
    /// </summary>
    public static bool IsInternalAddress(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }
        return ip.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xFE) == 0xFC;
    }

    /// <summary>Whether a resolved address may be used for <paramref name="host"/>.</summary>
    internal static bool IsAcceptable(string host, IPAddress ip) =>
        !PdfCryptoHelper.IsPrivateOrLocalAddress(ip) || (IsAllowed(host) && IsInternalAddress(ip));

    private static string Normalize(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    private static bool IsValidName(string name)
    {
        var labels = name.Split('.');
        return labels.Length >= 2
            && labels.All(l => Label.IsMatch(l))
            // An all-digit last label is an IP address, not a name.
            && !labels[^1].All(char.IsAsciiDigit)
            && name != "localhost"
            && !name.EndsWith(".localhost", StringComparison.Ordinal);
    }
}
