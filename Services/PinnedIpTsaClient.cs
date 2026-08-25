using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using DotNetSigningServer.Exceptions;
using iText.Signatures;

namespace DotNetSigningServer.Services;

/// <summary>
/// A <see cref="TSAClientBouncyCastle"/> that closes the DNS-rebinding window on
/// caller-supplied TSA URLs.
///
/// The SSRF guard in <see cref="PdfCryptoHelper"/> resolves the host once at
/// validation time. The stock iText client then re-resolves the same host when it
/// opens the connection, so an attacker-controlled zero-TTL domain can answer with
/// a public address during validation and an internal one (169.254.169.254, RFC1918)
/// at connect time.
///
/// This client re-checks the actual <see cref="IPEndPoint"/> inside
/// <see cref="SocketsHttpHandler.ConnectCallback"/> — i.e. at the moment the socket
/// is opened — and fails closed if it points anywhere private/local. Host and SNI
/// stay intact because HTTPS is negotiated by <see cref="HttpClient"/> on top of the
/// raw stream we hand back.
/// </summary>
public class PinnedIpTsaClient : TSAClientBouncyCastle
{
    private const string TimestampQueryContentType = "application/timestamp-query";

    // Shared handler/client: SocketsHttpHandler pools connections, so allocating one
    // per signing request would exhaust sockets under load.
    private static readonly HttpClient SharedClient = CreateGuardedClient();

    public PinnedIpTsaClient(string url, string? username, string? password)
        : base(url, username, password)
    {
    }

    protected override byte[] GetTSAResponse(byte[] requestBytes)
    {
        // iText's ITSAClient contract is synchronous; this runs on a request thread
        // that is already blocked inside the signing pipeline.
        return GetTsaResponseAsync(requestBytes).GetAwaiter().GetResult();
    }

    private async Task<byte[]> GetTsaResponseAsync(byte[] requestBytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, tsaURL)
        {
            Content = new ByteArrayContent(requestBytes)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(TimestampQueryContentType);
        request.Content.Headers.Add("Content-Transfer-Encoding", "binary");

        if (!string.IsNullOrEmpty(tsaUsername))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{tsaUsername}:{tsaPassword ?? string.Empty}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        using var response = await SharedClient.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

        // Mirror the stock client: some TSAs answer base64-encoded.
        var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
        if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            responseBytes = Convert.FromBase64String(Encoding.ASCII.GetString(responseBytes));
        }

        return responseBytes;
    }

    private static HttpClient CreateGuardedClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;

                IPAddress[] addresses;
                if (IPAddress.TryParse(host, out var literal))
                {
                    addresses = new[] { literal };
                }
                else
                {
                    addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                }

                // Fail closed: every candidate must be public, and the one we connect
                // to is re-checked immediately before the socket is opened.
                var target = addresses.FirstOrDefault(a => !PdfCryptoHelper.IsPrivateOrLocalAddress(a))
                             ?? throw new ApiValidationException("TSA_HOST_NOT_ALLOWED");

                if (PdfCryptoHelper.IsPrivateOrLocalAddress(target))
                {
                    throw new ApiValidationException("TSA_HOST_NOT_ALLOWED");
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), cancellationToken)
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

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }
}
