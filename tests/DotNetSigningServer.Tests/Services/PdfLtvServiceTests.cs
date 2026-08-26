using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Commons.Bouncycastle.Cert;
using iText.Signatures;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.X509;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// B-LT means the revocation evidence travels inside the document, so it can be
/// verified once the certificate has expired and the authority has stopped
/// answering. The tests use stub revocation clients: the self-signed certificate
/// the suite generates publishes no OCSP responder or CRL distribution point, so
/// there is nothing real to ask.
/// </summary>
public class PdfLtvServiceTests
{
    /// <summary>Returns a fixed OCSP response for any certificate, or none at all.</summary>
    private sealed class StubOcspClient : IOcspClient
    {
        private readonly byte[]? _response;
        internal StubOcspClient(byte[]? response) => _response = response;
        public byte[]? GetEncoded(IX509Certificate checkCert, IX509Certificate issuerCert, string url) => _response;
    }

    private sealed class StubCrlClient : ICrlClient
    {
        private readonly ICollection<byte[]> _crls;
        internal StubCrlClient(params byte[][] crls) => _crls = crls;
        public ICollection<byte[]> GetEncoded(IX509Certificate checkCert, string url) => _crls;
    }

    private static PdfSigningService CreateSigningService() =>
        new(
            TestHelpers.WrapOptions(new SealOptions()),
            TestHelpers.WrapOptions(new EvidenceOptions()),
            new PdfVisualSigningService());

    private static (byte[] Pdf, byte[] Crl) SignedPdfWithCrl()
    {
        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();
        var signed = CreateSigningService().SignWithPfx(new PfxSignInput
        {
            PdfContent = TestHelpers.CreateMinimalPdfBase64(),
            PfxContent = pfxBase64,
            PfxPassword = password,
            FieldName = "Signature1",
            SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 },
        });
        return (Convert.FromBase64String(signed), BuildCrl(pfxBase64, password));
    }

    private static byte[] SignedPdf() => SignedPdfWithCrl().Pdf;

    /// <summary>
    /// A genuine, signed CRL listing no revocations. iText parses and validates
    /// whatever the revocation clients hand back, so a stub returning arbitrary
    /// bytes is silently discarded — the evidence has to be real for the DSS to
    /// be written at all.
    /// </summary>
    private static byte[] BuildCrl(string pfxBase64, string password)
    {
        using var pfx = new MemoryStream(Convert.FromBase64String(pfxBase64));
        var store = new Pkcs12StoreBuilder().Build();
        store.Load(pfx, password.ToCharArray());
        var alias = store.Aliases.Cast<string>().First(store.IsKeyEntry);
        AsymmetricKeyParameter privateKey = store.GetKey(alias).Key;
        var certificate = store.GetCertificate(alias).Certificate;

        var generator = new X509V2CrlGenerator();
        // Self-signed test certificate: it is its own issuer, so it signs its own CRL.
        generator.SetIssuerDN(certificate.SubjectDN);
        generator.SetThisUpdate(DateTime.UtcNow.AddMinutes(-5));
        generator.SetNextUpdate(DateTime.UtcNow.AddYears(1));

        return generator
            .Generate(new Asn1SignatureFactory("SHA256WithRSA", privateKey))
            .GetEncoded();
    }

    [Fact]
    public void Extend_UnsignedDocument_IsRejected()
    {
        var service = new PdfLtvService(new StubOcspClient(new byte[] { 1, 2, 3 }), new StubCrlClient());
        var unsigned = Convert.FromBase64String(TestHelpers.CreateMinimalPdfBase64());

        var ex = Assert.Throws<ApiValidationException>(
            () => service.Extend(unsigned, tsaClient: null, addArchiveTimestamp: false));

        Assert.Equal("NO_SIGNATURE_TO_EXTEND", ex.Code);
    }

    /// <summary>
    /// The failure that matters most: when nothing could be collected, the caller
    /// must hear about it. Writing an empty DSS would hand back a document that
    /// looks long-term-valid and is not — and a renewal job would count it as done.
    /// </summary>
    [Fact]
    public void Extend_NoRevocationDataAvailable_IsRejectedRatherThanWritingAnEmptyDss()
    {
        var service = new PdfLtvService(new StubOcspClient(null), new StubCrlClient());

        var ex = Assert.Throws<ApiValidationException>(
            () => service.Extend(SignedPdf(), tsaClient: null, addArchiveTimestamp: false));

        Assert.Equal("NO_REVOCATION_DATA_AVAILABLE", ex.Code);
    }

    [Fact]
    public void Extend_WithoutTsa_RefusesToProduceAnArchiveTimestamp()
    {
        var service = new PdfLtvService(new StubOcspClient(new byte[] { 1, 2, 3 }), new StubCrlClient());

        var ex = Assert.Throws<ApiValidationException>(
            () => service.Extend(SignedPdf(), tsaClient: null, addArchiveTimestamp: true));

        Assert.Equal("TSA_REQUIRED_FOR_ARCHIVE_TIMESTAMP", ex.Code);
    }

    [Fact]
    public void Extend_EmbedsValidationDataAndKeepsTheSignatureIntact()
    {
        var (pdf, crl) = SignedPdfWithCrl();
        var service = new PdfLtvService(new StubOcspClient(null), new StubCrlClient(crl));

        var extended = service.Extend(pdf, tsaClient: null, addArchiveTimestamp: false);

        var result = PdfSignatureInspector.Inspect(extended);
        Assert.True(result.HasDss, "extending to B-LT must write a DSS dictionary");

        var signature = Assert.Single(result.Signatures);
        Assert.Equal("B-LT", signature.Level);
        // Validation data is added as an incremental update, so the original
        // signature must still verify afterwards.
        Assert.True(signature.IntegrityVerified);
        Assert.Equal("Signature1", signature.FieldName);
    }

    /// <summary>
    /// A real archive timestamp needs an RFC 3161 authority, so this runs only
    /// when one is named — the suite must not depend on the network.
    ///
    /// Run with: TSA_INTEGRATION_URL=https://freetsa.org/tsr dotnet test
    /// </summary>
    [Fact]
    public void Extend_WithTsa_ProducesBltaWithAnInvisibleArchiveTimestamp()
    {
        var tsaUrl = Environment.GetEnvironmentVariable("TSA_INTEGRATION_URL");
        if (string.IsNullOrWhiteSpace(tsaUrl)) return;

        var (pdf, crl) = SignedPdfWithCrl();
        var service = new PdfLtvService(new StubOcspClient(null), new StubCrlClient(crl));
        var tsaClient = PdfCryptoHelper.CreateTsaClient(tsaUrl);

        var extended = service.Extend(pdf, tsaClient, addArchiveTimestamp: true);

        var result = PdfSignatureInspector.Inspect(extended);
        Assert.True(result.HasDss);
        Assert.Equal(1, result.ArchiveTimestampCount);

        var signature = result.Signatures.Single(s => !s.IsDocumentTimestamp);
        Assert.Equal("B-LTA", signature.Level);

        var archiveTimestamp = result.Signatures.Single(s => s.IsDocumentTimestamp);
        Assert.Equal("ETSI.RFC3161", archiveTimestamp.SubFilter);
        // The date the next renewal is scheduled from.
        Assert.NotNull(archiveTimestamp.TimestampCertificateNotAfter);
        Assert.True(archiveTimestamp.TimestampCertificateNotAfter > DateTime.UtcNow);
    }

    /// <summary>
    /// Renewal is the same call run again: each pass lays down a fresh archive
    /// timestamp over everything before it, which is what keeps the chain alive
    /// as certificates age.
    /// </summary>
    [Fact]
    public void Extend_RunTwice_ChainsASecondArchiveTimestamp()
    {
        var tsaUrl = Environment.GetEnvironmentVariable("TSA_INTEGRATION_URL");
        if (string.IsNullOrWhiteSpace(tsaUrl)) return;

        var (pdf, crl) = SignedPdfWithCrl();
        var service = new PdfLtvService(new StubOcspClient(null), new StubCrlClient(crl));
        var tsaClient = PdfCryptoHelper.CreateTsaClient(tsaUrl);

        var first = service.Extend(pdf, tsaClient, addArchiveTimestamp: true);
        var renewed = service.Extend(first, tsaClient, addArchiveTimestamp: true);

        var result = PdfSignatureInspector.Inspect(renewed);
        Assert.Equal(2, result.ArchiveTimestampCount);
        Assert.All(result.Signatures, s => Assert.True(s.IntegrityVerified));
    }
}
