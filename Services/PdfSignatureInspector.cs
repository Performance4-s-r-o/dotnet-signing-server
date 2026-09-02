using DotNetSigningServer.Models;
using iText.Kernel.Pdf;
using iText.Signatures;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.X509;

namespace DotNetSigningServer.Services;

/// <summary>
/// Reads back what is already inside a signed PDF: which signatures it carries,
/// what PAdES level they reach, and — the reason this exists — when the
/// certificates behind their timestamps expire.
///
/// A B-LTA document has to be re-timestamped before the certificate of its last
/// archive timestamp expires, because once it has, no fresh revocation data can
/// be obtained for it and the chain of evidence is broken for good. Nothing can
/// schedule that renewal without reading these dates out of the document, so
/// this reports the facts and leaves the scheduling policy to the caller.
/// </summary>
public static class PdfSignatureInspector
{
    private const string PadesSubFilter = "ETSI.CAdES.detached";
    private const string TimestampSubFilter = "ETSI.RFC3161";

    /// <summary>RFC 3161 signature-timestamp attribute (id-aa-signatureTimeStampToken).</summary>
    private static readonly DerObjectIdentifier SignatureTimeStampOid = new("1.2.840.113549.1.9.16.2.14");

    public static SignatureInspectionResult Inspect(byte[] pdfBytes)
    {
        using var stream = new MemoryStream(pdfBytes);
        using var reader = new PdfReader(stream);
        using var document = new PdfDocument(reader);

        var util = new SignatureUtil(document);
        var names = util.GetSignatureNames();
        var signatures = new List<SignatureInspectionEntry>();

        foreach (var name in names)
        {
            signatures.Add(Describe(util, name));
        }

        var hasDss = document.GetCatalog().GetPdfObject().ContainsKey(new PdfName("DSS"));
        var archiveTimestamps = signatures.Count(s => s.IsDocumentTimestamp);

        // Revisions are ordered, so anything signed after the newest archive
        // timestamp is outside its byte range and therefore unprotected.
        var newestArchiveRevision = signatures
            .Where(s => s.IsDocumentTimestamp)
            .Select(s => s.Revision)
            .DefaultIfEmpty(0)
            .Max();
        var hasUnprotected = archiveTimestamps > 0
            && signatures.Any(s => !s.IsDocumentTimestamp && s.Revision > newestArchiveRevision);

        foreach (var signature in signatures)
        {
            signature.Level = DetermineLevel(signature, hasDss, archiveTimestamps);
        }

        return new SignatureInspectionResult
        {
            Signatures = signatures,
            HasDss = hasDss,
            ArchiveTimestampCount = archiveTimestamps,
            HasUnprotectedSignature = hasUnprotected,
            // The document can only be renewed as late as its earliest-expiring
            // timestamp certificate allows, so that is the date to plan against.
            EarliestTimestampCertificateExpiry = signatures
                .Select(s => s.TimestampCertificateNotAfter)
                .Where(d => d.HasValue)
                .Select(d => d!.Value)
                .DefaultIfEmpty()
                .Min() is { Ticks: > 0 } earliest
                ? earliest
                : null,
        };
    }

    private static SignatureInspectionEntry Describe(SignatureUtil util, string fieldName)
    {
        var dictionary = util.GetSignatureDictionary(fieldName);
        var subFilter = dictionary?.GetAsName(PdfName.SubFilter)?.GetValue();
        var entry = new SignatureInspectionEntry
        {
            FieldName = fieldName,
            SubFilter = subFilter,
            IsDocumentTimestamp = string.Equals(subFilter, TimestampSubFilter, StringComparison.Ordinal),
            Revision = util.GetRevision(fieldName),
        };

        try
        {
            var pkcs7 = util.ReadSignatureData(fieldName);
            entry.SignerName = pkcs7.GetSigningCertificate()?.GetSubjectDN()?.ToString();
            entry.DigestAlgorithm = pkcs7.GetDigestAlgorithmName();
            entry.SignatureAlgorithm = pkcs7.GetSignatureAlgorithmName();
            entry.SignedAt = Normalize(pkcs7.GetSignDate());
            entry.CoversWholeDocument = util.SignatureCoversWholeDocument(fieldName);
            entry.IntegrityVerified = pkcs7.VerifySignatureIntegrityAndAuthenticity();
            entry.SigningCertificateNotAfter = NotAfter(pkcs7.GetSigningCertificate());
        }
        catch (Exception)
        {
            // A signature we cannot parse is still worth reporting — the caller
            // needs to know it is there. Its fields simply stay null.
            entry.IntegrityVerified = false;
        }

        var container = dictionary?.GetAsString(PdfName.Contents)?.GetValueBytes();
        if (container != null)
        {
            ReadTimestampDetails(entry, container);
        }

        return entry;
    }

    /// <summary>
    /// Pulls the timestamp token out of the signature container and reads the
    /// expiry of the certificate that signed it. For a document timestamp the
    /// container is the token itself; for an ordinary signature the token sits in
    /// the unsigned signature-timestamp attribute.
    /// </summary>
    private static void ReadTimestampDetails(SignatureInspectionEntry entry, byte[] container)
    {
        try
        {
            // /Contents is a fixed-size reservation padded with zeros, which the
            // ASN.1 parser rejects outright — re-encode the first object to drop it.
            using var asn1 = new Asn1InputStream(container);
            var der = asn1.ReadObject()?.GetEncoded();
            if (der == null) return;

            var signedData = new CmsSignedData(der);
            TimeStampToken? token = null;

            if (entry.IsDocumentTimestamp)
            {
                token = new TimeStampToken(signedData);
            }
            else
            {
                var signer = signedData.GetSignerInfos().GetSigners().Cast<SignerInformation>().FirstOrDefault();
                var attribute = signer?.UnsignedAttributes?[SignatureTimeStampOid];
                if (attribute?.AttrValues?.Count > 0)
                {
                    var tokenBytes = Org.BouncyCastle.Asn1.Cms.ContentInfo.GetInstance(attribute.AttrValues[0]);
                    token = new TimeStampToken(new CmsSignedData(tokenBytes.GetEncoded()));
                }
            }

            if (token == null) return;

            entry.HasTimestamp = true;
            entry.TimestampedAt = Normalize(token.TimeStampInfo.GenTime);

            var tsaCertificate = FindSignerCertificate(token);
            // The certificate is the better name when it is there, but a TSA
            // answering with certReq=false embeds none — and then TSTInfo still
            // names itself. Falling back keeps those documents from reading as
            // "authority not stated" when the document does state one.
            entry.TimestampAuthority =
                tsaCertificate?.SubjectDN?.ToString() ?? TsaNameFromTokenInfo(token);
            entry.TimestampCertificateNotAfter = NotAfter(tsaCertificate);
            entry.TimestampCertificateNotBefore =
                tsaCertificate == null ? null : Normalize(tsaCertificate.NotBefore);
        }
        catch (Exception)
        {
            // Leave the timestamp fields unset — an unreadable token must not
            // make the whole document unreadable.
        }
    }

    /// <summary>
    /// The authority named inside TSTInfo. Optional in RFC 3161 and, like the
    /// certificate subject, asserted by the document rather than proven.
    /// </summary>
    private static string? TsaNameFromTokenInfo(TimeStampToken token)
    {
        try
        {
            return token.TimeStampInfo.Tsa?.Name?.ToString();
        }
        catch (Exception)
        {
            // A malformed GeneralName is not a reason to lose the rest.
            return null;
        }
    }

    /// <summary>The certificate the timestamp authority signed the token with.</summary>
    private static X509Certificate? FindSignerCertificate(TimeStampToken token)
    {
        var matches = token.GetCertificates().EnumerateMatches(token.SignerID);
        return matches.FirstOrDefault();
    }

    private static DateTime? NotAfter(X509Certificate? certificate) =>
        certificate == null ? null : Normalize(certificate.NotAfter);

    private static DateTime? NotAfter(iText.Commons.Bouncycastle.Cert.IX509Certificate? certificate)
    {
        if (certificate == null) return null;
        try
        {
            return Normalize(certificate.GetNotAfter());
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? Normalize(DateTime value) =>
        value == default ? null : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

    /// <summary>
    /// A structural reading of the PAdES level, not a conformance verdict — it
    /// says what the document carries, not whether a validator will accept it.
    /// </summary>
    private static string DetermineLevel(SignatureInspectionEntry entry, bool hasDss, int archiveTimestamps)
    {
        if (entry.IsDocumentTimestamp)
        {
            return "archive-timestamp";
        }

        if (!string.Equals(entry.SubFilter, PadesSubFilter, StringComparison.Ordinal))
        {
            // adbe.pkcs7.detached and friends: a valid PDF signature, but outside
            // the PAdES baseline profiles.
            return "non-PAdES";
        }

        if (hasDss && archiveTimestamps > 0) return "B-LTA";
        if (hasDss) return "B-LT";
        return entry.HasTimestamp ? "B-T" : "B-B";
    }
}
