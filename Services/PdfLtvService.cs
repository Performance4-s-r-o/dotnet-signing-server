using DotNetSigningServer.Exceptions;
using iText.Kernel.Pdf;
using iText.Signatures;
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
    /// </summary>
    public byte[] Extend(byte[] signedPdf, ITSAClient? tsaClient, bool addArchiveTimestamp)
    {
        // Checked before any of the work below: collecting revocation data means
        // a round trip per responder, and spending those only to refuse the
        // request afterwards would be wasteful and slow to fail.
        if (addArchiveTimestamp && tsaClient == null)
        {
            throw new ApiValidationException("TSA_REQUIRED_FOR_ARCHIVE_TIMESTAMP");
        }

        var withValidationData = AddValidationData(signedPdf);

        return addArchiveTimestamp
            ? AddArchiveTimestamp(withValidationData, tsaClient!)
            : withValidationData;
    }

    /// <summary>
    /// Writes a DSS dictionary holding the certificate chain plus OCSP responses
    /// and CRLs for every signature the document already carries.
    /// </summary>
    private byte[] AddValidationData(byte[] signedPdf)
    {
        using var input = new MemoryStream(signedPdf);
        using var output = new MemoryStream();

        var reader = new PdfReader(input);
        var writer = new PdfWriter(output);
        // Append mode: existing signatures must survive untouched, which is the
        // whole point of adding validation data rather than re-signing.
        var document = new PdfDocument(reader, writer, new StampingProperties().UseAppendMode());

        var verification = new LtvVerification(document);
        var signatureNames = new SignatureUtil(document).GetSignatureNames();
        if (signatureNames.Count == 0)
        {
            throw new ApiValidationException("NO_SIGNATURE_TO_EXTEND");
        }

        var added = false;
        foreach (var name in signatureNames)
        {
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
