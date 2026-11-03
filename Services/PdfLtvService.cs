using DotNetSigningServer.Exceptions;
using iText.Commons.Bouncycastle.Cert;
using iText.Kernel.Pdf;
using iText.Signatures;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Ocsp;
using Org.BouncyCastle.Ocsp;
using Org.BouncyCastle.Tsp;

namespace DotNetSigningServer.Services;

/// <summary>
/// Raises a signed document from B-T to B-LT and B-LTA.
///
/// B-T proves when a document was signed. It does not prove the certificate was
/// still valid at that moment — that needs revocation data, which is only
/// available online and which certificate authorities stop publishing once a
/// certificate expires. B-LT freezes that evidence into the document itself, so
/// it can be verified years later with nothing but the file.
///
/// B-LTA then timestamps the whole package, DSS included, so the evidence is
/// protected once the algorithms and certificates behind it start to age.
/// </summary>
public class PdfLtvService
{
    private readonly IOcspClient _ocspClient;
    private readonly ICrlClient _crlClient;

    /// <param name="ocspClient">Injectable so tests can supply canned revocation data — a self-signed certificate has no responder to ask.</param>
    public PdfLtvService(IOcspClient? ocspClient = null, ICrlClient? crlClient = null)
    {
        _ocspClient = ocspClient ?? new GuardedOcspClient();
        _crlClient = crlClient ?? new GuardedCrlClient();
    }

    /// <summary>
    /// Embeds revocation data for every signature in the document (B-LT), and
    /// optionally adds an archive timestamp over the result (B-LTA).
    ///
    /// The order matters and is not interchangeable: the validation data has to be
    /// written first so that the archive timestamp covers it. A timestamp applied
    /// before the DSS would leave the evidence it is meant to protect outside its
    /// own coverage.
    ///
    /// <paramref name="requireCompleteFor"/> names a signature that must end up
    /// fully covered: every certificate of its chain and of its signature
    /// timestamp (trust anchors aside) has OCSP or a CRL, or the call fails with
    /// <c>INCOMPLETE_REVOCATION_DATA</c>. Without it the call is best effort and
    /// passes as soon as any signature got any data.
    /// </summary>
    public byte[] Extend(byte[] signedPdf, ITSAClient? tsaClient, bool addArchiveTimestamp, string? requireCompleteFor = null)
    {
        // Checked before any of the work below: collecting revocation data means
        // a round trip per responder, and spending those only to refuse the
        // request afterwards would be wasteful and slow to fail.
        if (addArchiveTimestamp && tsaClient == null)
        {
            throw new ApiValidationException("TSA_REQUIRED_FOR_ARCHIVE_TIMESTAMP");
        }

        // The whole operation shares one fetch budget (#72): at most 20 revocation downloads in 30 s.
        // The archive timestamp below goes to the TSA, which has its own timeout and breaker.
        byte[] withValidationData;
        using (RevocationFetchPolicy.Begin())
        {
            withValidationData = AddValidationData(signedPdf, requireCompleteFor);
        }

        return addArchiveTimestamp
            ? AddArchiveTimestamp(withValidationData, tsaClient!)
            : withValidationData;
    }

    /// <summary>
    /// Writes a DSS dictionary holding the certificate chain plus OCSP responses
    /// and CRLs for every signature the document already carries.
    /// </summary>
    private byte[] AddValidationData(byte[] signedPdf, string? requireCompleteFor)
    {
        using var input = new MemoryStream(signedPdf);
        using var output = new MemoryStream();

        var reader = new PdfReader(input);
        var writer = new PdfWriter(output);
        // Append mode: existing signatures must survive untouched, which is the
        // whole point of adding validation data rather than re-signing.
        var document = new PdfDocument(reader, writer, new StampingProperties().UseAppendMode());

        var verification = new LtvVerification(document);
        var signatureUtil = new SignatureUtil(document);
        var signatureNames = signatureUtil.GetSignatureNames();
        if (signatureNames.Count == 0)
        {
            throw new ApiValidationException("NO_SIGNATURE_TO_EXTEND");
        }
        if (requireCompleteFor != null && !signatureNames.Contains(requireCompleteFor))
        {
            throw new ApiValidationException("SIGNATURE_NOT_FOUND", $"No signature named '{requireCompleteFor}'");
        }

        var added = false;
        foreach (var name in signatureNames)
        {
            DotNetSigningServer.Services.Operations.OperationDeadline.ThrowIfCancelled();
            if (name == requireCompleteFor)
            {
                AddCompleteVerification(verification, signatureUtil, name);
                added = true;
                continue;
            }
            // Best effort for the others: an earlier signer's CA being offline
            // must not block a later signature, so their partial data still counts.
            // WHOLE_CHAIN: a verifier years from now needs the intermediates too,
            // not just the leaf. OCSP_OPTIONAL_CRL takes OCSP where the CA offers
            // it and falls back to a CRL where it does not.
            added |= verification.AddVerification(
                name,
                _ocspClient,
                _crlClient,
                LtvVerification.CertificateOption.WHOLE_CHAIN,
                LtvVerification.Level.OCSP_OPTIONAL_CRL,
                LtvVerification.CertificateInclusion.YES);
        }

        if (!added)
        {
            // Every responder was unreachable, blocked by the SSRF guard, or the
            // certificate published none. Saying so beats writing an empty DSS and
            // letting the document claim a long-term validity it does not have.
            document.Close();
            throw new ApiValidationException("NO_REVOCATION_DATA_AVAILABLE");
        }

        verification.Merge();
        document.Close();
        return output.ToArray();
    }

    /// <summary>
    /// Validation data for one signature, all or nothing. iText's own
    /// <c>AddVerification</c> reports success when any certificate got data, and
    /// with <c>WHOLE_CHAIN</c> never looks at the signature timestamp's
    /// certificates (only <c>ALL_CERTIFICATES</c> does, and that one also fetches
    /// issuers from AIA outside the revocation guard). So the chain is walked
    /// here with the same guarded clients and handed to iText ready-made.
    ///
    /// A certificate counts as covered with a "good" OCSP answer or at least one
    /// CRL. Self-signed certificates other than the signer's are trust anchors
    /// and need none. Only certificates the signature carries are checked: an
    /// issuer missing from the CMS or the token cannot be looked up here.
    /// </summary>
    private void AddCompleteVerification(LtvVerification verification, SignatureUtil signatureUtil, string name)
    {
        var pkcs7 = signatureUtil.ReadSignatureData(name);
        var signingCertificate = pkcs7.GetSigningCertificate();
        var carried = new List<IX509Certificate>();
        foreach (var certificate in pkcs7.GetCertificates().Concat(pkcs7.GetTimestampCertificates() ?? Array.Empty<IX509Certificate>()))
        {
            if (!carried.Any(c => c.Equals(certificate)))
            {
                carried.Add(certificate);
            }
        }

        var ocsps = new List<byte[]>();
        var crls = new List<byte[]>();
        var missing = new List<string>();
        foreach (var certificate in carried)
        {
            DotNetSigningServer.Services.Operations.OperationDeadline.ThrowIfCancelled();
            if (!certificate.Equals(signingCertificate) && IsSelfSigned(certificate))
            {
                continue;
            }
            if (Bc(certificate).GetExtensionValue(new DerObjectIdentifier(ShortTermValidityOid)) != null)
            {
                // ETSI short-term certificate: validity is assured without revocation data.
                continue;
            }

            var issuer = carried.FirstOrDefault(c => !c.Equals(certificate) && IsIssuerOf(c, certificate));
            var ocsp = issuer == null ? null : _ocspClient.GetEncoded(certificate, issuer, null);
            if (ocsp != null && IsGood(ocsp, certificate))
            {
                ocsps.Add(ocsp);
                continue;
            }

            var found = _crlClient.GetEncoded(certificate, null);
            if (found != null && found.Count > 0)
            {
                foreach (var crl in found.Where(crl => !crls.Any(known => known.SequenceEqual(crl))))
                {
                    crls.Add(crl);
                }
                continue;
            }

            missing.Add(certificate.GetSubjectDN()?.ToString() ?? "(no subject)");
        }

        if (missing.Count > 0)
        {
            throw new ApiValidationException(
                "INCOMPLETE_REVOCATION_DATA",
                $"No revocation data for: {string.Join("; ", missing)}");
        }

        verification.AddVerification(name, ocsps, crls, carried.Select(c => c.GetEncoded()).ToList());
    }

    private const string ShortTermValidityOid = "0.4.0.194121.2.1";

    /// <summary>A basic OCSP response that says "good" about this certificate's serial number.</summary>
    private static bool IsGood(byte[] basicOcspResponse, IX509Certificate certificate)
    {
        try
        {
            var response = new BasicOcspResp(BasicOcspResponse.GetInstance(Asn1Object.FromByteArray(basicOcspResponse)));
            var serial = Bc(certificate).SerialNumber;
            return response.Responses.Any(single =>
                single.GetCertID().SerialNumber.Equals(serial) && single.GetCertStatus() == CertificateStatus.Good);
        }
        catch
        {
            return false;
        }
    }

    private static Org.BouncyCastle.X509.X509Certificate Bc(IX509Certificate certificate) =>
        ((iText.Bouncycastle.X509.X509CertificateBC)certificate).GetCertificate();

    private static bool IsSelfSigned(IX509Certificate certificate) => IsIssuerOf(certificate, certificate);

    private static bool IsIssuerOf(IX509Certificate candidate, IX509Certificate certificate)
    {
        if (!string.Equals(candidate.GetSubjectDN()?.ToString(), certificate.GetIssuerDN()?.ToString(), StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            certificate.Verify(candidate.GetPublicKey());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Adds an invisible RFC 3161 document timestamp covering the whole file.
    /// Invisible on purpose: a maintained document is re-timestamped every few
    /// years and a visible mark per renewal would slowly bury the page.
    /// </summary>
    private static byte[] AddArchiveTimestamp(byte[] pdf, ITSAClient tsaClient)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        var reader = new PdfReader(input);
        var signer = new PdfSigner(reader, output, new StampingProperties().UseAppendMode());
        var fieldName = $"ArchiveTimestamp_{Guid.NewGuid():N}";

        try
        {
            signer.Timestamp(tsaClient, fieldName);
        }
        catch (Exception ex) when (FindTspException(ex) is TspException)
        {
            throw new TsaCommunicationException(
                "(archive timestamp)",
                $"The timestamping authority rejected the archive timestamp request: {ex.Message}",
                ex);
        }

        return output.ToArray();
    }

    private static TspException? FindTspException(Exception? ex)
    {
        while (ex != null)
        {
            if (ex is TspException tsp) return tsp;
            ex = ex.InnerException;
        }
        return null;
    }
}
