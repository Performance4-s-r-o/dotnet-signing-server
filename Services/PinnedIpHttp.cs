using System.Net;
using System.Net.Sockets;
using DotNetSigningServer.Exceptions;

namespace DotNetSigningServer.Services;

/// <summary>
/// Outbound HTTP whose destination address is re-checked at the moment the socket
/// opens, not just when the URL was validated.
///
/// Every guard in this codebase that resolves a caller-supplied host has the same
/// hole otherwise: validation resolves DNS once, the HTTP stack resolves again when
/// it connects, and an attacker-controlled zero-TTL domain can answer with a public
/// address the first time and an internal one (169.254.169.254, RFC1918) the second.
///
/// Handing back the socket ourselves closes that window. Host and SNI are untouched,
/// because TLS is still negotiated by <see cref="HttpClient"/> on top of the raw
/// stream, so certificate validation keeps working against the real hostname.
/// </summary>
internal static class PinnedIpHttp
{
    /// <param name="errorCode">
    /// Thrown as <see cref="ApiValidationException"/> when the resolved address is
    /// private. Callers pass their own code so the refusal stays legible to whoever
    /// asked — a TSA problem and a revocation problem need different answers.
    /// </param>
    internal static HttpClient CreateClient(string errorCode, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            // SocketsHttpHandler pools connections; one handler per request would
            // exhaust sockets under load, so callers keep these in static fields.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var target = await ResolvePublicAddressAsync(
                    context.DnsEndPoint.Host, errorCode, cancellationToken).ConfigureAwait(false);

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket
                        .ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), cancellationToken)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>
    /// Picks an address to connect to, failing closed: a host with no public address
    /// is refused rather than silently falling back to whatever DNS returned first.
    /// </summary>
    private static async Task<IPAddress> ResolvePublicAddressAsync(
        string host, string errorCode, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }

        var target = addresses.FirstOrDefault(a => !PdfCryptoHelper.IsPrivateOrLocalAddress(a));
        if (target is null || PdfCryptoHelper.IsPrivateOrLocalAddress(target))
        {
            throw new ApiValidationException(errorCode);
        }

        return target;
    }
}
