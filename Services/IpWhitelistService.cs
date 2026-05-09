using System.Net;
using DotNetSigningServer.Models;

namespace DotNetSigningServer.Services;

public interface IIpWhitelistService
{
    bool IsIpAllowedForToken(IPAddress? clientIp, ApiToken token);
    (List<string> Valid, List<string> Invalid) ParseAndValidateIps(string? rawIps);
}

public class IpWhitelistService : IIpWhitelistService
{
    public bool IsIpAllowedForToken(IPAddress? clientIp, ApiToken token)
    {
        if (string.IsNullOrWhiteSpace(token.AllowedIps))
        {
            return true; // no restriction
        }

        if (clientIp == null)
        {
            return false;
        }

        var normalized = NormalizeIp(clientIp);
        var entries = SplitEntries(token.AllowedIps);

        foreach (var entry in entries)
        {
            if (entry.Contains('/'))
            {
                if (IPNetwork.TryParse(entry, out var network) && network.Contains(normalized))
                {
                    return true;
                }
            }
            else
            {
                if (IPAddress.TryParse(entry, out var address) && NormalizeIp(address).Equals(normalized))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public (List<string> Valid, List<string> Invalid) ParseAndValidateIps(string? rawIps)
    {
        var valid = new List<string>();
        var invalid = new List<string>();

        if (string.IsNullOrWhiteSpace(rawIps))
        {
            return (valid, invalid);
        }

        var entries = SplitEntries(rawIps);

        foreach (var entry in entries)
        {
            if (entry.Contains('/'))
            {
                if (IPNetwork.TryParse(entry, out _))
                    valid.Add(entry);
                else
                    invalid.Add(entry);
            }
            else
            {
                if (IPAddress.TryParse(entry, out _))
                    valid.Add(entry);
                else
                    invalid.Add(entry);
            }
        }

        return (valid, invalid);
    }

    private static IPAddress NormalizeIp(IPAddress ip)
    {
        // Map IPv4-mapped IPv6 (::ffff:x.x.x.x) to plain IPv4
        if (ip.IsIPv4MappedToIPv6)
        {
            return ip.MapToIPv4();
        }
        return ip;
    }

    private static IEnumerable<string> SplitEntries(string raw)
    {
        return raw.Split(new[] { '\n', '\r', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                  .Select(e => e.Trim())
                  .Where(e => e.Length > 0);
    }
}
