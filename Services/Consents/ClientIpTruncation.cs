using System.Net;
using System.Net.Sockets;

namespace DotNetSigningServer.Services.Consents;

/// <summary>Shortens a client IP before it leaves for the backoffice service (GDPR minimisation).</summary>
public static class ClientIpTruncation
{
    /// <summary>IPv4: last octet zeroed (/24). IPv6: kept to /48. Null for empty or malformed input.</summary>
    public static string? Truncate(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var text = ip.Trim();
        // A zone id ("fe80::1%eth0") is local detail, not part of the address.
        var zone = text.IndexOf('%');
        if (zone >= 0) text = text[..zone];
        if (!IPAddress.TryParse(text.Trim('[', ']'), out var address)) return null;

        // IPv4-mapped IPv6 (::ffff:a.b.c.d) is treated as the IPv4 address.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            bytes[3] = 0;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            for (var i = 6; i < bytes.Length; i++) bytes[i] = 0;
        }
        else return null;

        return new IPAddress(bytes).ToString();
    }
}
