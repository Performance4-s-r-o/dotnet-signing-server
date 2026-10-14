using System.Net;

namespace DotNetSigningServer.Middleware;

/// <summary>
/// The address a rate limit counts by. The origin is reachable through the Cloudflare
/// tunnel only, so <c>CF-Connecting-IP</c> is the client; without it (internal calls from
/// the portal over the Docker network, local development) it is the peer address after
/// the forwarded-headers middleware. A value that does not parse is ignored.
/// </summary>
public static class ClientIp
{
    public static string Of(HttpContext context)
    {
        var header = context.Request.Headers["CF-Connecting-IP"].ToString();
        if (!string.IsNullOrWhiteSpace(header) && IPAddress.TryParse(header.Trim(), out var cf))
        {
            return cf.ToString();
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
