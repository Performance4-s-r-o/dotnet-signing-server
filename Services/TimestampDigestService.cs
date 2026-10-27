using System.Security.Cryptography;
using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using iText.Signatures;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Tsp;

namespace DotNetSigningServer.Services;

/// <summary>
/// RFC 3161 over a digest the caller supplies (/api/timestamp-digest). The authority is
/// asked through the same clients as every other timestamp, so the HTTPS and address
/// checks, the connect-time pinning, the backup and the circuit breaker all apply.
/// </summary>
public static class TimestampDigestService
{
    public const int DigestBytes = 32;
    public const string DigestInvalid = "DIGEST_INVALID";
    public const string TsaUrlRequired = "TSA_URL_REQUIRED";
    public const string TsaUrlInvalid = "TSA_URL_INVALID";
    public const string TsaPortNotAllowed = "TSA_PORT_NOT_ALLOWED";

    /// <summary>At most this many stamps in flight on this path; each holds a pool thread while the authority answers.</summary>
    public const int MaxConcurrent = 8;
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    public static readonly SemaphoreSlim Gate = new(MaxConcurrent, MaxConcurrent);

    /// <summary>64 hex characters or base64 (padded or not) of exactly 32 bytes; anything else is refused.</summary>
    public static byte[] ParseDigest(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) throw new ApiValidationException(DigestInvalid);

        byte[]? bytes = null;
        if (text.Length == DigestBytes * 2 && text.All(Uri.IsHexDigit))
        {
            bytes = Convert.FromHexString(text);
        }
        else if (text.Length is 43 or 44)
        {
            try { bytes = Convert.FromBase64String(text.Length == 43 ? text + "=" : text); }
            catch (FormatException) { /* falls through to the refusal */ }
        }

        if (bytes is not { Length: DigestBytes }) throw new ApiValidationException(DigestInvalid);
        return bytes;
    }

    public static ITSAClient CreateClient(TimestampDigestInput input)
    {
        if (string.IsNullOrWhiteSpace(input.TsaUrl)) throw new ApiValidationException(TsaUrlRequired);
        EnsureUrlAllowed(input.TsaUrl);
        if (!string.IsNullOrWhiteSpace(input.TsaBackupUrl)) EnsureUrlAllowed(input.TsaBackupUrl);

        return PdfCryptoHelper.CreateTsaClient(
            input.TsaUrl,
            string.IsNullOrWhiteSpace(input.TsaUsername) ? null : input.TsaUsername,
            string.IsNullOrWhiteSpace(input.TsaPassword) ? null : input.TsaPassword,
            TsaEndpoint.From(input.TsaBackupUrl, input.TsaBackupUsername, input.TsaBackupPassword))
            ?? throw new ApiValidationException(TsaUrlRequired);
    }

    /// <summary>
    /// Port 443 only and no credentials in the URL (they would end up in logs and in the error
    /// body). A URL that does not parse falls through to the HTTPS check in CreateTsaClient.
    /// </summary>
    private static void EnsureUrlAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new ApiValidationException(TsaUrlInvalid);
        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !uri.IsDefaultPort)
        {
            throw new ApiValidationException(TsaPortNotAllowed);
        }
    }

    public static TimestampDigestResult Stamp(ITSAClient client, byte[] digest, string tsaUrl)
    {
        // Free and takes any URL and password: like /api/tsa-probe it must not consult or feed
        // the breaker, or anyone could open it for a platform authority.
        using var noBreaker = TsaCircuitBreaker.Bypass();

        byte[] tokenBytes;
        TimeStampToken token;
        try
        {
            tokenBytes = client.GetTimeStampToken(digest);
            if (tokenBytes == null || tokenBytes.Length == 0)
            {
                throw new TsaCommunicationException(tsaUrl, "The timestamp authority returned an empty token.");
            }
            token = new TimeStampToken(new CmsSignedData(tokenBytes));
        }
        catch (TsaCommunicationException) { throw; }
        catch (Exception ex) when (ex is ApiValidationException or OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new TsaCommunicationException(tsaUrl,
                $"The timestamp authority is unreachable or rejected the request: {ex.Message}", ex);
        }

        // The imprint in the token is what the authority actually stamped.
        var info = token.TimeStampInfo;
        var stamped = info.GetMessageImprintDigest();
        if (!string.Equals(info.MessageImprintAlgOid, "2.16.840.1.101.3.4.2.1", StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(stamped, digest))
        {
            throw new TsaCommunicationException(tsaUrl, "The timestamp token does not cover the digest that was sent.");
        }

        string? subject = null;
        try
        {
            subject = token.GetCertificates().EnumerateMatches(token.SignerID)
                .FirstOrDefault()?.SubjectDN?.ToString();
        }
        catch (Exception) { /* a token without a readable certificate still stamps the digest */ }
        subject ??= info.Tsa?.Name?.ToString();

        return new TimestampDigestResult
        {
            Token = Convert.ToBase64String(tokenBytes),
            GenTime = DateTime.SpecifyKind(info.GenTime, DateTimeKind.Utc),
            TsaSubject = subject,
        };
    }
}
