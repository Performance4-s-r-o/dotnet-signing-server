using System.Net.Http.Headers;
using System.Text;
using DotNetSigningServer.Services.Operations;
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

    public PinnedIpTsaClient(string url, string? username, string? password)
        : base(url, username, password)
    {
    }

    protected override byte[] GetTSAResponse(byte[] requestBytes)
    {
        // The breaker comes first: an authority that has just failed N times in a row is not
        // asked again for a while, which also hands a primary's turn to the backup at once (#72).
        var host = Uri.TryCreate(tsaURL, UriKind.Absolute, out var uri) ? uri.Host : tsaURL;
        var breaker = TsaCircuitBreaker.Default;
        breaker.Acquire(host);

        // From here the authority may issue (and bill) a token whatever comes back.
        OperationDeadline.MarkExternalCall();
        try
        {
            // iText's ITSAClient contract is synchronous; this runs on a request thread
            // that is already blocked inside the signing pipeline.
            var response = GetTsaResponseAsync(requestBytes).GetAwaiter().GetResult();
            breaker.RecordSuccess(host);
            return response;
        }
        catch (OperationCanceledException) when (OperationDeadline.CurrentToken.IsCancellationRequested)
        {
            // Our own side gave up (deadline, caller): says nothing about the authority.
            breaker.Release(host);
            throw;
        }
        catch
        {
            breaker.RecordFailure(host);
            throw;
        }
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

        // Per request rather than HttpClient.Timeout, so the deadline is configurable at startup
        // on a client that is shared and created once.
        // Linked to the operation's deadline, so a request that ran out of time stops waiting here.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(OperationDeadline.CurrentToken);
        deadline.CancelAfter(RequestTimeout);
        using var response = await SharedClient.SendAsync(request, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseBytes = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);

        // Mirror the stock client: some TSAs answer base64-encoded.
        var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
        if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            responseBytes = Convert.FromBase64String(Encoding.ASCII.GetString(responseBytes));
        }

        return responseBytes;
    }

    /// <summary>
    /// Per authority, default 10 s; <c>Tsa:TimeoutSeconds</c> (env <c>TSA_TIMEOUT_SECONDS</c>),
    /// applied at startup. The deadlines have to nest — portal (60 s) &gt; this server's
    /// /api/sign &gt; TSA — and a backup is tried after the primary, so two of these must fit
    /// well inside the portal's wait (#77). At 30 s each, a slow primary plus backup outlasted
    /// the portal and the signature finished for nobody. Applies to every TSA call: signing,
    /// timestamps, extend-signature (LTV) and the probe.
    /// </summary>
    internal static TimeSpan RequestTimeout
    {
        get => _requestTimeout;
        set => _requestTimeout = value > TimeSpan.Zero && value <= MaxRequestTimeout
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"TSA timeout must be within (0, {MaxRequestTimeout}].");
    }

    private static TimeSpan _requestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound for <see cref="RequestTimeout"/>; also the shared client's own timeout.</summary>
    internal static readonly TimeSpan MaxRequestTimeout = TimeSpan.FromSeconds(120);

    // Shared handler/client: SocketsHttpHandler pools connections, so allocating one
    // per signing request would exhaust sockets under load.
    private static readonly HttpClient SharedClient = CreateGuardedClient();

    private static HttpClient CreateGuardedClient() =>
        PinnedIpHttp.CreateClient("TSA_HOST_NOT_ALLOWED", MaxRequestTimeout);
}
