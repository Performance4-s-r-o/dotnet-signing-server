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
        var peer = context.Connection.RemoteIpAddress;

        // Forwarded-headers middleware replaces RemoteIpAddress with the X-Forwarded-For value
        // and keeps the socket's own peer in X-Original-For.
        var original = context.Request.Headers["X-Original-For"].ToString();
        var socketPeer = !string.IsNullOrWhiteSpace(original) && IPAddress.TryParse(original.Split(':')[0].Trim('[', ']'), out var parsed)
            ? parsed
            : peer;

        // CF-Connecting-IP is believed only from a peer on a private network (cloudflared in the
        // Docker network, the reverse proxy). From anywhere else it is just a header somebody wrote.
        var header = context.Request.Headers["CF-Connecting-IP"].ToString();
        if (socketPeer is not null
            && Services.PdfCryptoHelper.IsPrivateOrLocalAddress(socketPeer)
            && !string.IsNullOrWhiteSpace(header)
            && IPAddress.TryParse(header.Trim(), out var cf))
        {
            return cf.ToString();
        }
        return peer?.ToString() ?? "unknown";
    }
}
