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
    internal static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(3);

    /// <summary>DNS lookup used by the guards; replaced only by tests.</summary>
    internal static Func<string, CancellationToken, Task<IPAddress[]>> Resolver { get; set; } =
        (host, ct) => Dns.GetHostAddressesAsync(host, ct);

    /// <param name="errorCode">
    /// Thrown as <see cref="ApiValidationException"/> when the resolved address is
    /// private. Callers pass their own code so the refusal stays legible to whoever
    /// asked — a TSA problem and a revocation problem need different answers.
    /// </param>
    /// <param name="acceptable">
    /// Which resolved address may be used for a host; default = public only. The TSA client
    /// passes <see cref="TsaPrivateHosts.IsAcceptable"/>.
    /// </param>
    internal static HttpClient CreateClient(
        string errorCode, TimeSpan timeout, Func<string, IPAddress, bool>? acceptable = null)
    {
        acceptable ??= (_, ip) => !PdfCryptoHelper.IsPrivateOrLocalAddress(ip);
        var handler = new SocketsHttpHandler
        {
            // SocketsHttpHandler pools connections; one handler per request would
            // exhaust sockets under load, so callers keep these in static fields.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            // A redirect is a second destination the certificate's owner chose, and it would not
            // go through the address check of the first (#72). The authorities and responders we
            // talk to answer where they were addressed.
            AllowAutoRedirect = false,
            MaxResponseHeadersLength = 32, // KB
            ConnectCallback = async (context, cancellationToken) =>
            {
                var target = await ResolvePublicAddressAsync(
                    context.DnsEndPoint.Host, errorCode, acceptable, cancellationToken).ConfigureAwait(false);

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
    internal static async Task<IPAddress> ResolvePublicAddressAsync(
        string host, string errorCode, Func<string, IPAddress, bool> acceptable, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            // A resolver that does not answer must not hold the request for its own timeout.
            using var dnsDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dnsDeadline.CancelAfter(DnsTimeout);
            try
            {
                addresses = await Resolver(host, dnsDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ApiValidationException(errorCode);
            }
        }

        var target = addresses.FirstOrDefault(a => acceptable(host, a));
        if (target is null || !acceptable(host, target))
        {
            throw new ApiValidationException(errorCode);
        }

        return target;
    }
}
