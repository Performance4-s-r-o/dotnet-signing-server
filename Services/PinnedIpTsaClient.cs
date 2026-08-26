using System.Net.Http.Headers;
using System.Text;
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
/// The pinning itself lives in <see cref="PinnedIpHttp"/>, shared with the client
/// that fetches revocation data — both face the same caller-chosen-destination
/// problem, and one implementation is one place to get it right.
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

    private static HttpClient CreateGuardedClient() =>
        PinnedIpHttp.CreateClient("TSA_HOST_NOT_ALLOWED", TimeSpan.FromSeconds(30));
}
