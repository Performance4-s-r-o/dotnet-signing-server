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
    public const string TsaNotConfigured = "TSA_NOT_CONFIGURED";

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
        if (string.IsNullOrWhiteSpace(input.TsaUrl)) throw new ApiValidationException(TsaNotConfigured);

        return PdfCryptoHelper.CreateTsaClient(
            input.TsaUrl,
            string.IsNullOrWhiteSpace(input.TsaUsername) ? null : input.TsaUsername,
            string.IsNullOrWhiteSpace(input.TsaPassword) ? null : input.TsaPassword,
            TsaEndpoint.From(input.TsaBackupUrl, input.TsaBackupUsername, input.TsaBackupPassword))
            ?? throw new ApiValidationException(TsaNotConfigured);
    }

    public static TimestampDigestResult Stamp(ITSAClient client, byte[] digest, string tsaUrl)
    {
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
