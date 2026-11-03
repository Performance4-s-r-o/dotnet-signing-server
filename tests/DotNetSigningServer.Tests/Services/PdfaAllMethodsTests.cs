using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Kernel.Pdf;
using iText.Signatures;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Pkcs;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// #184 for every signing path: the preparation of presign in front of the timestamp and the
/// visual signature, and PDF/A-3 keeping embedded files — the attachment and the evidence a seal
/// carries. Checked with <see cref="PdfAConformanceCheck"/>; set <c>PDFA_DUMP_DIR</c> to keep the
/// results for veraPDF.
/// </summary>
public class PdfaAllMethodsTests : IDisposable
{
    private readonly List<string> _files = new();
    private static readonly LimitsOptions Limits = new() { RejectActiveContent = true };
    private static readonly PdfSafetyGuard Guard = new(TestHelpers.WrapOptions(Limits));
    private static readonly PdfConversionService Conversion = new(NullLogger<PdfConversionService>.Instance);

    /// <summary>1×1 RGBA PNG: an image with an alpha channel (soft mask in the PDF).</summary>
    private const string TransparentPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private static readonly byte[] Evidence = System.Text.Encoding.UTF8.GetBytes("{\"evidence\":\"ses\",\"v\":1}");

    public void Dispose()
    {
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch { }
        }
    }

    private static void Dump(string name, byte[] pdf)
    {
        var dir = Environment.GetEnvironmentVariable("PDFA_DUMP_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(System.IO.Path.Combine(dir, name + ".pdf"), pdf);
    }

    private static PdfSigningService Service() =>
        new(TestHelpers.WrapOptions(new EvidenceOptions()), new PdfVisualSigningService());

    private static DocumentPreparation.Result Prepare(byte[] pdf, string conformance, bool flatten = true) =>
        DocumentPreparation.Prepare(pdf, new VisualSignInput { FlattenForm = flatten, ConvertToPdfa = true, PdfaConformance = conformance },
            Limits, Guard, Conversion, "test");

    private static PdfAConformance? Declared(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return doc.GetConformance().GetAConformance();
    }

    private static byte[] Attach(byte[] pdf, string fileName, byte[] content, string mime) =>
        Convert.FromBase64String(Service().AddAttachment(new AddAttachmentInput
        {
            AttachmentContent = Convert.ToBase64String(content),
            FileName = fileName,
            Description = "SES evidence manifest",
            MimeType = mime,
        }, pdf));

    private static byte[] FileContent(byte[] pdf, string key)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var (_, spec) = EmbeddedFiles.Of(doc).Single(f => f.Key == key);
        return spec.GetAsDictionary(PdfName.EF)!.GetAsStream(PdfName.F)!.GetBytes();
    }

    private static SignRect Rect => new() { X = 50, Y = 100, Width = 200, Height = 60 };

    // ── timestamp ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("PDF/A-2B")]
    [InlineData("PDF/A-3B")]
    public void Timestamp_of_a_prepared_document_stays_PDFA(string conformance)
    {
        var prepared = Prepare(PresignPdfaTests.ScriptedFormEmbedded(), conformance);
        var input = new DocumentTimestampInput { Reason = "Razítko", Location = "Praha", SignRect = Rect, SignImageContent = TransparentPng };

        // No font family: an embedded one is used in a PDF/A document.
        var stamped = Convert.FromBase64String(Service().ApplyDocumentTimestamp(input, new LocalTsaClient(), prepared.Pdf, prepared.KeepPdfA));

        Dump("timestamp-" + conformance.Replace("/", ""), stamped);
        Assert.Equal(prepared.KeepPdfA, Declared(stamped));
        Assert.Empty(PdfAConformanceCheck.Problems(stamped, prepared.KeepPdfA!));
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(stamped)));
        var util = new SignatureUtil(doc);
        var name = util.GetSignatureNames().Single();
        Assert.True(util.SignatureCoversWholeDocument(name));
        Assert.True(util.ReadSignatureData(name).VerifySignatureIntegrityAndAuthenticity());
    }

    // ── visual signature ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("PDF/A-2B")]
    [InlineData("PDF/A-3B")]
    public void Visual_signature_of_a_prepared_document_stays_PDFA(string conformance)
    {
        var prepared = Prepare(PresignPdfaTests.ScriptedFormEmbedded(), conformance);
        var input = new VisualSignInput
        {
            Reason = "Souhlas",
            Location = "Praha",
            SignerName = "Jana Nováková",
            SignRect = Rect,
            SignImageContent = TransparentPng,
        };

        var drawn = Convert.FromBase64String(Service().ApplyVisualSign(input, prepared.Pdf, prepared.KeepPdfA));

        Dump("visual-" + conformance.Replace("/", ""), drawn);
        Assert.Equal(prepared.KeepPdfA, Declared(drawn));
        Assert.Empty(PdfAConformanceCheck.Problems(drawn, prepared.KeepPdfA!));
    }

    // ── embedded files and the seal ─────────────────────────────────────────────────────────────

    [Fact]
    public void PDFA_2B_refuses_embedded_files_with_its_own_code()
    {
        var withEvidence = Attach(PresignPdfaTests.ScriptedFormEmbedded(), "ses-evidence.p7m", Evidence, "application/pkcs7-mime");
        var ex = Assert.Throws<ApiValidationException>(() => Prepare(withEvidence, "PDF/A-2B"));
        Assert.Equal(DocumentPreparation.EmbeddedFilesCode, ex.Code);
    }

    [Fact]
    public void PDFA_3B_keeps_every_embedded_file_byte_for_byte_as_an_associated_file()
    {
        var pdf = Attach(PresignPdfaTests.ScriptedFormEmbedded(), "smlouva-priloha.txt", new byte[] { 1, 2, 3 }, "text/plain");
        pdf = Attach(pdf, "ses-evidence.p7m", Evidence, "application/pkcs7-mime");

        var prepared = Prepare(pdf, "PDF/A-3B");

        Assert.Equal(DocumentPreparation.PdfaOutcome.Converted, prepared.Report.Pdfa);
        Assert.Empty(PdfAConformanceCheck.Problems(prepared.Pdf, PdfAConformance.PDF_A_3B));
        Assert.Equal(new byte[] { 1, 2, 3 }, FileContent(prepared.Pdf, "smlouva-priloha.txt"));
        Assert.Equal(Evidence, FileContent(prepared.Pdf, "ses-evidence.p7m"));
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(prepared.Pdf)));
        Assert.Equal(2, doc.GetCatalog().GetPdfObject().GetAsArray(PdfName.AF)!.Size());
    }

    private (string Path, string Hash, string CertPem, string Pfx, string Password) Presign(byte[] pdf, PdfAConformance? keep, string field)
    {
        var (certPem, pfx, password) = TestHelpers.CreateTestCertificate();
        var input = new PreSignInput
        {
            CertificatePem = certPem,
            Reason = "Pečeť",
            Location = "Praha",
            SignRect = Rect,
            SignImageContent = TransparentPng,
            VerificationUrl = "https://example.org/verify/abc",
            VerificationMode = "qr",
        };
        var (path, hash) = Service().HandlePreSign(input, pdf, field, keep);
        _files.Add(path);
        return (path, hash, certPem, pfx, password);
    }

    private static string SignHash(string hashHex, string pfx, string password)
    {
        using var pfxMs = new MemoryStream(Convert.FromBase64String(pfx));
        var store = new Pkcs12StoreBuilder().Build();
        store.Load(pfxMs, password.ToCharArray());
        var alias = store.Aliases.Cast<string>().First(store.IsKeyEntry);
        var signer = Org.BouncyCastle.Security.SignerUtilities.GetSigner("SHA256withRSA");
        signer.Init(true, store.GetKey(alias).Key);
        var hash = Convert.FromHexString(hashHex);
        signer.BlockUpdate(hash, 0, hash.Length);
        return Convert.ToHexString(signer.GenerateSignature());
    }

    private byte[] Seal(byte[] pdf, string field)
    {
        // The portal's seal: evidence attached, then presign with PDF/A, then the key signs the hash.
        var withEvidence = Attach(pdf, "ses-evidence-" + field + ".p7m", Evidence, "application/pkcs7-mime");
        var prepared = Prepare(withEvidence, "PDF/A-3B");
        var (path, hash, certPem, pfx, password) = Presign(prepared.Pdf, prepared.KeepPdfA, field);
        return Convert.FromBase64String(Service().HandleSign(
            new SignInput { Id = "x", SignedHash = SignHash(hash, pfx, password) }, path, certPem, field));
    }

    [Fact]
    public void Seal_with_its_evidence_as_PDFA_3B_keeps_the_evidence_and_a_valid_signature_then_a_second_seal_too()
    {
        var first = Seal(PresignPdfaTests.ScriptedFormEmbedded(), "Seal1");
        Dump("seal-PDFA-3B", first);
        Assert.Equal(PdfAConformance.PDF_A_3B, Declared(first));
        Assert.Empty(PdfAConformanceCheck.Problems(first, PdfAConformance.PDF_A_3B));
        Assert.Equal(Evidence, FileContent(first, "ses-evidence-Seal1.p7m"));

        // The second signer: the document is signed and PDF/A-3B, the new evidence is appended.
        var second = Seal(first, "Seal2");
        Dump("seal-PDFA-3B-second", second);
        Assert.Empty(PdfAConformanceCheck.Problems(second, PdfAConformance.PDF_A_3B));
        Assert.Equal(Evidence, FileContent(second, "ses-evidence-Seal1.p7m"));
        Assert.Equal(Evidence, FileContent(second, "ses-evidence-Seal2.p7m"));
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(second)));
        var util = new SignatureUtil(doc);
        Assert.Equal(new[] { "Seal1", "Seal2" }, util.GetSignatureNames().OrderBy(n => n).ToArray());
        foreach (var name in util.GetSignatureNames())
        {
            Assert.True(util.ReadSignatureData(name).VerifySignatureIntegrityAndAuthenticity());
        }
        Assert.True(util.SignatureCoversWholeDocument("Seal2"));
    }

    [Fact]
    public void Evidence_added_to_a_signed_PDFA_2B_is_refused_at_presign_not_signed_as_broken_PDFA()
    {
        var prepared = Prepare(PresignPdfaTests.ScriptedFormEmbedded(), "PDF/A-2B");
        var signed = PresignPdfaTests.SignedWithPfx(prepared.Pdf);
        var withEvidence = Attach(signed, "ses-evidence.p7m", Evidence, "application/pkcs7-mime");

        var again = Prepare(withEvidence, "PDF/A-2B");
        Assert.Equal(DocumentPreparation.PdfaOutcome.AlreadyPdfa, again.Report.Pdfa);
        var ex = Assert.Throws<ApiValidationException>(() => Presign(again.Pdf, again.KeepPdfA, "Seal2"));
        Assert.Equal(DocumentPreparation.NotPreservedCode, ex.Code);
    }

    [Fact]
    public void Attachment_on_a_PDFA_3B_document_keeps_it_PDFA_3B()
    {
        var prepared = Prepare(PresignPdfaTests.ScriptedFormEmbedded(), "PDF/A-3B");

        var attached = Attach(prepared.Pdf, "priloha.bin", new byte[] { 9, 8, 7 }, "");

        Dump("attachment-PDFA-3B", attached);
        Assert.Empty(PdfAConformanceCheck.Problems(attached, PdfAConformance.PDF_A_3B));
        Assert.Equal(new byte[] { 9, 8, 7 }, FileContent(attached, "priloha.bin"));
    }

    /// <summary>Answers every certificate with one empty CRL: enough for iText to write a DSS.</summary>
    private sealed class AnyCrl : ICrlClient
    {
        private readonly byte[] _crl;

        public AnyCrl()
        {
            var keys = new Org.BouncyCastle.Crypto.Generators.RsaKeyPairGenerator();
            keys.Init(new Org.BouncyCastle.Crypto.KeyGenerationParameters(new Org.BouncyCastle.Security.SecureRandom(), 2048));
            var pair = keys.GenerateKeyPair();
            var generator = new Org.BouncyCastle.X509.X509V2CrlGenerator();
            generator.SetIssuerDN(new Org.BouncyCastle.Asn1.X509.X509Name("CN=Test CRL"));
            generator.SetThisUpdate(DateTime.UtcNow.AddMinutes(-5));
            generator.SetNextUpdate(DateTime.UtcNow.AddDays(1));
            _crl = generator.Generate(new Org.BouncyCastle.Crypto.Operators.Asn1SignatureFactory("SHA256WithRSA", pair.Private)).GetEncoded();
        }

        public ICollection<byte[]> GetEncoded(iText.Commons.Bouncycastle.Cert.IX509Certificate checkCert, string url) => new List<byte[]> { _crl };
    }

    [Fact]
    public void Seal_extended_to_long_term_validation_stays_PDFA_3B()
    {
        // The platform seal is extended to B-LT after signing (a DSS in an incremental update).
        var sealed_ = Seal(PresignPdfaTests.ScriptedFormEmbedded(), "PlatformSeal1");

        var extended = new PdfLtvService(null, new AnyCrl()).Extend(sealed_, tsaClient: null, addArchiveTimestamp: false);

        Dump("seal-PDFA-3B-ltv", extended);
        Assert.Empty(PdfAConformanceCheck.Problems(extended, PdfAConformance.PDF_A_3B));
        Assert.Equal(Evidence, FileContent(extended, "ses-evidence-PlatformSeal1.p7m"));
    }
}
