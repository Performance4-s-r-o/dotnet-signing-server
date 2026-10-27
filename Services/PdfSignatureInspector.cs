using System.Security.Cryptography;
using DotNetSigningServer.Models;
using iText.IO.Source;
using iText.Kernel.Pdf;
using iText.Signatures;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.X509;

namespace DotNetSigningServer.Services;

/// <summary>
/// Reads back what is already inside a signed PDF: which signatures it carries,
/// what PAdES level each of them structurally reaches, which revisions the file
/// has, and when the certificates behind its timestamps expire.
///
/// A B-LTA document has to be re-timestamped before the certificate of its last
/// archive timestamp expires, because once it has, no fresh revocation data can
/// be obtained for it and the chain of evidence is broken for good. Nothing can
/// schedule that renewal without reading these dates out of the document, so
/// this reports the facts and leaves the scheduling policy to the caller.
///
/// Nothing here is a trust verdict. No certificate chain is validated against a
/// trust list and no revocation status is checked. What is checked is limited to
/// the bytes of the file: signature integrity, ByteRange shape, and whether a
/// timestamp token is signed by the certificate it carries and stamps the value
/// it claims to stamp.
/// </summary>
public static class PdfSignatureInspector
{
    private const string PadesSubFilter = "ETSI.CAdES.detached";
    private const string TimestampSubFilter = "ETSI.RFC3161";

    /// <summary>
    /// Revisions after a signature are opened one by one to find where its
    /// validation data was added. Past this many revisions in the file the work
    /// is skipped and no signature is reported as B-LT or higher.
    /// </summary>
    internal const int MaxRevisionsForValidationData = 100;

    /// <summary>RFC 3161 signature-timestamp attribute (id-aa-signatureTimeStampToken).</summary>
    private static readonly DerObjectIdentifier SignatureTimeStampOid = new("1.2.840.113549.1.9.16.2.14");

    private static readonly PdfName DssName = new("DSS");
    private static readonly PdfName VriName = new("VRI");
    private static readonly PdfName CertsName = new("Certs");

    /// <summary>Facts about one signature that the response does not carry.</summary>
    private sealed class Working
    {
        public required SignatureInspectionEntry Entry { get; init; }
        /// <summary>End of the signed bytes, only when the ByteRange is sane.</summary>
        public long? SignedEnd { get; set; }
        public byte[]? SigningCertificate { get; set; }
        /// <summary>VRI keys this signature may be filed under (SHA-1 of /Contents, padded and trimmed).</summary>
        public HashSet<string> VriKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The validation data present at the end of one revision.</summary>
    private sealed record DssSnapshot(bool HasVri, HashSet<string> VriKeys, HashSet<string> CertificateHashes);

    public static SignatureInspectionResult Inspect(byte[] pdfBytes)
    {
        using var stream = new MemoryStream(pdfBytes, writable: false);
        using var reader = new PdfReader(stream);
        using var document = new PdfDocument(reader);

        var util = new SignatureUtil(document);
        var working = util.GetSignatureNames().Select(name => Describe(util, name, pdfBytes)).ToList();
        var signatures = working.Select(w => w.Entry).ToList();

        var hasDss = document.GetCatalog().GetPdfObject().ContainsKey(DssName);
        var archiveTimestamps = signatures.Count(s => s.IsDocumentTimestamp);

        var revisionEnds = PdfRevisionScanner.FindRevisionEnds(
            pdfBytes, working.Where(w => w.SignedEnd.HasValue).Select(w => w.SignedEnd!.Value));
        var revisionHashes = PdfRevisionScanner.HashPrefixes(pdfBytes, revisionEnds);
        var revisions = revisionEnds
            .Select((end, i) => new InspectedRevision { Revision = i + 1, EndOffset = end, Sha256 = revisionHashes[i] })
            .ToList();

        foreach (var w in working.Where(w => w.SignedEnd.HasValue))
        {
            var index = revisionEnds.IndexOf(w.SignedEnd!.Value);
            w.Entry.FileRevision = index >= 0 ? index + 1 : null;
        }

        var dssAt = hasDss && revisionEnds.Count <= MaxRevisionsForValidationData
            ? ValidationDataReader(pdfBytes, revisionEnds)
            : null;

        foreach (var w in working)
        {
            w.Entry.StructuralLevel = DetermineLevel(w, working, revisionEnds, dssAt);
        }

        // Revisions are ordered, so anything signed after the newest archive
        // timestamp is outside its byte range and therefore unprotected.
        var newestArchiveRevision = signatures
            .Where(s => s.IsDocumentTimestamp)
            .Select(s => s.Revision)
            .DefaultIfEmpty(0)
            .Max();
        var hasUnprotected = archiveTimestamps > 0
            && signatures.Any(s => !s.IsDocumentTimestamp && s.Revision > newestArchiveRevision);

        return new SignatureInspectionResult
        {
            Signatures = signatures,
            HasDss = hasDss,
            ArchiveTimestampCount = archiveTimestamps,
            HasUnprotectedSignature = hasUnprotected,
            DocumentSha256 = Convert.ToHexString(SHA256.HashData(pdfBytes)).ToLowerInvariant(),
            ByteLength = pdfBytes.LongLength,
            Revisions = revisions,
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

    private static Working Describe(SignatureUtil util, string fieldName, byte[] pdf)
    {
        var dictionary = util.GetSignatureDictionary(fieldName);
        var subFilter = dictionary?.GetAsName(PdfName.SubFilter)?.GetValue();
        var entry = new SignatureInspectionEntry
        {
            FieldName = fieldName,
            SubFilter = subFilter,
            IsDocumentTimestamp = string.Equals(subFilter, TimestampSubFilter, StringComparison.Ordinal),
        };
        var working = new Working { Entry = entry };

        try
        {
            entry.Revision = util.GetRevision(fieldName);
        }
        catch (Exception)
        {
            // Revision numbering needs sortable byte ranges; a broken one leaves it 0.
        }

        var contents = dictionary?.GetAsString(PdfName.Contents)?.GetValueBytes();
        entry.ByteRange = ReadByteRange(dictionary);
        entry.ByteRangeSane = entry.ByteRange != null && contents != null
            && IsByteRangeSane(entry.ByteRange, contents, pdf);
        if (entry.ByteRangeSane)
        {
            working.SignedEnd = entry.ByteRange![2] + entry.ByteRange[3];
        }

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
            working.SigningCertificate = pkcs7.GetSigningCertificate()?.GetEncoded();
        }
        catch (Exception)
        {
            // A signature we cannot parse is still worth reporting — the caller
            // needs to know it is there. Its fields simply stay null.
            entry.IntegrityVerified = false;
        }

        if (contents != null)
        {
            working.VriKeys.Add(Sha1Hex(contents));
            ReadTimestampDetails(working, contents, pdf);
        }

        return working;
    }

    private static long[]? ReadByteRange(PdfDictionary? dictionary)
    {
        var array = dictionary?.GetAsArray(PdfName.ByteRange);
        if (array == null) return null;
        var values = new long[array.Size()];
        for (var i = 0; i < values.Length; i++)
        {
            var number = array.GetAsNumber(i);
            if (number == null) return null;
            var value = number.GetValue();
            if (value != Math.Floor(value) || value < long.MinValue || value > long.MaxValue) return null;
            values[i] = (long)value;
        }
        return values;
    }

    /// <summary>
    /// The only shape a PDF signature may have: two ranges, the first from the
    /// start of the file, the gap between them exactly this signature's
    /// <c>/Contents</c> hex string, and the second ending inside the file.
    /// Anything else either leaves bytes unsigned outside <c>/Contents</c> or
    /// points at bytes that are not there.
    /// </summary>
    internal static bool IsByteRangeSane(long[] range, byte[] contents, byte[] pdf)
    {
        if (range.Length != 4) return false;
        long a = range[0], b = range[1], c = range[2], d = range[3];
        if (a != 0 || b <= 0 || d < 0) return false;
        if (c <= b) return false; // overlap or no gap
        if (c > pdf.LongLength || d > pdf.LongLength || c + d > pdf.LongLength) return false;

        // The gap is "<hex>": angle brackets, hex digits, nothing else.
        if (pdf[b] != '<' || pdf[c - 1] != '>') return false;
        var hexLength = c - b - 2;
        if (hexLength <= 0 || hexLength % 2 != 0) return false;
        for (var i = b + 1; i < c - 1; i++)
        {
            if (!Uri.IsHexDigit((char)pdf[i])) return false;
        }

        // And it has to be this signature's /Contents, not another one.
        if (hexLength / 2 != contents.Length) return false;
        var hex = System.Text.Encoding.ASCII.GetString(pdf, (int)b + 1, (int)hexLength);
        return Convert.FromHexString(hex).AsSpan().SequenceEqual(contents);
    }

    /// <summary>
    /// Pulls the timestamp token out of the signature container, reads the
    /// expiry of the certificate that signed it, and checks the token.
    /// For a document timestamp the container is the token itself; for an
    /// ordinary signature the token sits in the unsigned signature-timestamp
    /// attribute.
    /// </summary>
    private static void ReadTimestampDetails(Working working, byte[] container, byte[] pdf)
    {
        var entry = working.Entry;
        try
        {
            // /Contents is a fixed-size reservation padded with zeros, which the
            // ASN.1 parser rejects outright — re-encode the first object to drop it.
            using var asn1 = new Asn1InputStream(container);
            var der = asn1.ReadObject()?.GetEncoded();
            if (der == null) return;
            working.VriKeys.Add(Sha1Hex(der));

            var signedData = new CmsSignedData(der);
            TimeStampToken? token = null;
            byte[]? signatureValue = null;

            if (entry.IsDocumentTimestamp)
            {
                token = new TimeStampToken(signedData);
            }
            else
            {
                var signer = signedData.GetSignerInfos().GetSigners().Cast<SignerInformation>().FirstOrDefault();
                signatureValue = signer?.GetSignature();
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

            entry.TimestampSignatureValid = TokenSignatureValid(token, tsaCertificate);
            entry.TimestampMessageImprintMatches = entry.IsDocumentTimestamp
                ? ImprintMatchesSignedBytes(token, entry, pdf)
                : ImprintMatches(token, signatureValue);
            entry.TimestampVerified =
                entry.TimestampSignatureValid == true && entry.TimestampMessageImprintMatches == true;
        }
        catch (Exception)
        {
            // Leave the timestamp fields unset — an unreadable token must not
            // make the whole document unreadable.
        }
    }

    /// <summary>
    /// Whether the token was signed by the certificate it carries: the CMS
    /// signature, the ESS certificate binding, a critical timeStamping usage and
    /// the certificate's validity at genTime. Null when the token carries no
    /// certificate, because then there is nothing to check it with.
    /// Says nothing about whether that certificate is trustworthy.
    /// </summary>
    private static bool? TokenSignatureValid(TimeStampToken token, X509Certificate? certificate)
    {
        if (certificate == null) return null;
        try
        {
            token.Validate(certificate);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A signature timestamp stamps the hash of the SignerInfo signature value.</summary>
    private static bool? ImprintMatches(TimeStampToken token, byte[]? signatureValue)
    {
        if (signatureValue == null) return null;
        var info = token.TimeStampInfo;
        var expected = DigestUtilities.CalculateDigest(info.MessageImprintAlgOid, signatureValue);
        return CryptographicOperations.FixedTimeEquals(expected, info.GetMessageImprintDigest());
    }

    /// <summary>A document timestamp stamps the hash of its own signed byte ranges.</summary>
    private static bool? ImprintMatchesSignedBytes(TimeStampToken token, SignatureInspectionEntry entry, byte[] pdf)
    {
        if (!entry.ByteRangeSane || entry.ByteRange == null) return null;
        var info = token.TimeStampInfo;
        var digest = DigestUtilities.GetDigest(info.MessageImprintAlgOid);
        var range = entry.ByteRange;
        digest.BlockUpdate(pdf, (int)range[0], (int)range[1]);
        digest.BlockUpdate(pdf, (int)range[2], (int)range[3]);
        var expected = DigestUtilities.DoFinal(digest);
        return CryptographicOperations.FixedTimeEquals(expected, info.GetMessageImprintDigest());
    }

    /// <summary>
    /// The DSS as it stood at the end of a revision (by index), read on first
    /// use and cached, so only the revisions a level actually depends on are
    /// opened. Null when the revision cannot be read or carries no DSS.
    /// </summary>
    private static Func<int, DssSnapshot?> ValidationDataReader(byte[] pdf, List<long> revisionEnds)
    {
        var cache = new Dictionary<int, DssSnapshot?>();
        return index =>
        {
            if (!cache.TryGetValue(index, out var snapshot))
            {
                snapshot = ReadDssAt(pdf, revisionEnds[index]);
                cache[index] = snapshot;
            }
            return snapshot;
        };
    }

    private static DssSnapshot? ReadDssAt(byte[] pdf, long end)
    {
        try
        {
            // A window over the same array, not a copy of the file per revision.
            // A fresh source each time: closing the reader closes its source.
            var file = new RandomAccessSourceFactory().CreateSource(pdf);
            using var reader = new PdfReader(new WindowRandomAccessSource(file, 0, end), new ReaderProperties());
            using var document = new PdfDocument(reader);

            var dss = document.GetCatalog().GetPdfObject().GetAsDictionary(DssName);
            if (dss == null) return null;

            var vri = dss.GetAsDictionary(VriName);
            var vriKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (vri != null)
            {
                foreach (var key in vri.KeySet()) vriKeys.Add(key.GetValue());
            }

            var certificates = new HashSet<string>(StringComparer.Ordinal);
            var certs = dss.GetAsArray(CertsName);
            if (certs != null)
            {
                for (var i = 0; i < certs.Size(); i++)
                {
                    var bytes = certs.GetAsStream(i)?.GetBytes();
                    if (bytes != null) certificates.Add(Convert.ToHexString(SHA256.HashData(bytes)));
                }
            }

            return new DssSnapshot(vri != null, vriKeys, certificates);
        }
        catch (Exception)
        {
            // A prefix that does not open as a PDF is not a revision we can read.
            return null;
        }
    }

    /// <summary>
    /// Whether this DSS covers the signature. With a VRI dictionary the
    /// signature must have its own entry: a writer that files validation data
    /// per signature and left this one out did not cover it. Without VRI (it is
    /// optional) the DSS must at least carry the signing certificate.
    /// </summary>
    private static bool Covers(DssSnapshot dss, Working signature)
    {
        if (dss.HasVri) return signature.VriKeys.Overlaps(dss.VriKeys);
        return signature.SigningCertificate != null
            && dss.CertificateHashes.Contains(Convert.ToHexString(SHA256.HashData(signature.SigningCertificate)));
    }

    /// <summary>
    /// A structural reading of the PAdES level of one signature, not a
    /// conformance verdict and not a statement of trust. Levels build on each
    /// other:
    /// <list type="bullet">
    /// <item>B-T: its own timestamp token verifies, or a verified document
    /// timestamp in a later revision covers it.</item>
    /// <item>B-LT: B-T, and validation data for this signature (its VRI entry,
    /// or its certificate in a DSS without VRI) was added after it.</item>
    /// <item>B-LTA: B-LT, and a verified document timestamp covers the revision
    /// that added that validation data.</item>
    /// </list>
    /// A token that is present but does not verify counts as no token.
    /// </summary>
    private static string DetermineLevel(
        Working signature,
        List<Working> all,
        List<long> revisionEnds,
        Func<int, DssSnapshot?>? dssAt)
    {
        var entry = signature.Entry;
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

        var verifiedDocumentTimestampEnds = all
            .Where(w => w.Entry.IsDocumentTimestamp && w.Entry.TimestampVerified && w.SignedEnd.HasValue)
            .Select(w => w.SignedEnd!.Value)
            .ToList();

        var signedEnd = signature.SignedEnd;
        var hasT = entry.TimestampVerified
            || (signedEnd.HasValue && verifiedDocumentTimestampEnds.Any(end => end > signedEnd.Value));
        if (!hasT) return "B-B";

        if (dssAt == null || !signedEnd.HasValue) return "B-T";

        long? coveredAt = null;
        for (var i = 0; i < revisionEnds.Count; i++)
        {
            if (revisionEnds[i] <= signedEnd.Value) continue;
            if (dssAt(i) is { } dss && Covers(dss, signature))
            {
                coveredAt = revisionEnds[i];
                break;
            }
        }
        if (coveredAt == null) return "B-T";

        entry.ValidationDataRevision = revisionEnds.IndexOf(coveredAt.Value) + 1;
        return verifiedDocumentTimestampEnds.Any(end => end >= coveredAt.Value) ? "B-LTA" : "B-LT";
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

    private static string Sha1Hex(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes));

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
}
