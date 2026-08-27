using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Kernel.Pdf;
using iText.Signatures;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// Every operation that touches an already-signed document has to be an
/// incremental update. Anything that rewrites the file re-serializes the
/// existing /Contents and destroys it — Adobe then reports
/// "SigDict /Contents illegal data" and every earlier signature is lost, which
/// is exactly what the verification step used to do to multi-signer documents.
///
/// These tests take a sealed document and run each of the remaining operations
/// over it: a signature made with the signer's own certificate (both the PFX and
/// the presign/sign flow the SharePoint extension uses), a document timestamp,
/// and the long-term-validity extension. The seal must survive all of them.
/// </summary>
public class MultiSignatureAccumulationTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var path in _tempFiles.Where(File.Exists))
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PfxSignature_OnSealedDocument_LeavesTheSealValid()
    {
        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();
        var sealed_ = Seal(TestHelpers.CreateMinimalPdfBase64(), pfxBase64, password);

        var signed = CreateSigningService().SignWithPfx(new PfxSignInput
        {
            PdfContent = sealed_,
            PfxContent = pfxBase64,
            PfxPassword = password,
            FieldName = "Signature1",
            SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 },
        });

        AssertEverySignatureStillValid(Convert.FromBase64String(signed), expectedCount: 2);
    }

    /// <summary>
    /// The flow the SharePoint extension drives: the private key stays on the
    /// user's device, so the server prepares the document, hands out a hash, and
    /// injects the container afterwards. Verification runs inside the presign
    /// step, which is where the document used to get rewritten.
    /// </summary>
    [Fact]
    public void ExternalSignature_OnSealedDocument_LeavesTheSealValid()
    {
        var (certPem, pfxBase64, password) = TestHelpers.CreateTestCertificate();
        var service = CreateSigningService();
        var sealed_ = Seal(TestHelpers.CreateMinimalPdfBase64(), pfxBase64, password);

        var (path, hashHex) = service.HandlePreSign(new PreSignInput
        {
            PdfContent = sealed_,
            CertificatePem = certPem,
            Location = "Unit Tests",
            Reason = "Own certificate",
            SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 },
            SignPageNumber = 1,
            VerificationUrl = "https://example.test/verify/second",
            VerificationMode = "qr",
        }, "Signature1");
        _tempFiles.Add(path);

        var signed = service.HandleSign(
            new SignInput { Id = "test", SignedHash = SignHash(hashHex, pfxBase64, password) },
            path,
            certPem,
            "Signature1");

        var bytes = Convert.FromBase64String(signed);
        AssertEverySignatureStillValid(bytes, expectedCount: 2);

        // The seal already added the QR page; the second signer must not add another.
        using var ms = new MemoryStream(bytes);
        using var reader = new PdfReader(ms);
        using var doc = new PdfDocument(reader);
        Assert.Equal(2, doc.GetNumberOfPages());
    }

    [Fact]
    public void DocumentTimestamp_OnSealedDocument_LeavesTheSealValid()
    {
        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();
        var sealed_ = Seal(TestHelpers.CreateMinimalPdfBase64(), pfxBase64, password);

        var timestamped = CreateSigningService().ApplyDocumentTimestamp(
            new DocumentTimestampInput
            {
                PdfContent = sealed_,
                SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 },
                SignPageNumber = 1,
                Visible = false,
            },
            new LocalTsaClient());

        var bytes = Convert.FromBase64String(timestamped);
        AssertEverySignatureStillValid(bytes, expectedCount: 2);

        var result = PdfSignatureInspector.Inspect(bytes);
        Assert.Equal(1, result.ArchiveTimestampCount);
    }

    /// <summary>
    /// Three seals in a row — what a document collects when several people sign
    /// it online. Each round runs the whole sealing pipeline, verification page
    /// included.
    /// </summary>
    [Fact]
    public void ThreeSealsInARow_AllRemainValid()
    {
        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();

        var pdf = TestHelpers.CreateMinimalPdfBase64();
        for (int round = 1; round <= 3; round++)
        {
            pdf = Seal(pdf, pfxBase64, password, verificationId: $"round-{round}");
        }

        var bytes = Convert.FromBase64String(pdf);
        AssertEverySignatureStillValid(bytes, expectedCount: 3);

        using var ms = new MemoryStream(bytes);
        using var reader = new PdfReader(ms);
        using var doc = new PdfDocument(reader);
        Assert.Equal(2, doc.GetNumberOfPages());
    }

    /// <summary>
    /// Adobe reports a broken container as "SigDict /Contents illegal data", i.e.
    /// a DER parse failure, so the raw bytes are checked before iText's reader is
    /// trusted to say anything about them.
    /// </summary>
    private static void AssertEverySignatureStillValid(byte[] pdfBytes, int expectedCount)
    {
        using var ms = new MemoryStream(pdfBytes);
        using var reader = new PdfReader(ms);
        using var doc = new PdfDocument(reader);

        var util = new SignatureUtil(doc);
        var names = util.GetSignatureNames();
        Assert.Equal(expectedCount, names.Count);

        foreach (var name in names)
        {
            var container = util.GetSignatureDictionary(name)
                .GetAsString(PdfName.Contents)
                .GetValueBytes();
            using var asn1 = new Asn1InputStream(container);
            Assert.NotNull(asn1.ReadObject());

            Assert.True(
                util.ReadSignatureData(name).VerifySignatureIntegrityAndAuthenticity(),
                $"signature '{name}' no longer verifies");
        }
    }

    private static string Seal(
        string pdfContent,
        string pfxBase64,
        string password,
        string verificationId = "first")
    {
        var options = new SealOptions
        {
            Enabled = true,
            PfxBase64 = pfxBase64,
            PfxPassword = password,
            Visible = false,
        };

        var service = new PdfSealingService(
            TestHelpers.WrapOptions(options),
            CreateSigningService(options),
            new PdfVisualSigningService());

        return service.ApplySeal(new SealInput
        {
            PdfContent = pdfContent,
            VerificationUrl = $"https://example.test/verify/{verificationId}",
            VerificationMode = "qr",
            Reason = "Corporate seal",
            Location = "Unit Tests",
        });
    }

    private static PdfSigningService CreateSigningService(SealOptions? sealOptions = null) =>
        new(
            TestHelpers.WrapOptions(sealOptions ?? new SealOptions()),
            TestHelpers.WrapOptions(new EvidenceOptions()),
            new PdfVisualSigningService());

    /// <summary>Stands in for the private key that never leaves the user's device.</summary>
    private static string SignHash(string hashHex, string pfxBase64, string password)
    {
        using var pfx = new MemoryStream(Convert.FromBase64String(pfxBase64));
        var store = new Pkcs12StoreBuilder().Build();
        store.Load(pfx, password.ToCharArray());
        var alias = store.Aliases.Cast<string>().First(store.IsKeyEntry);

        var hashBytes = new byte[hashHex.Length / 2];
        for (int i = 0; i < hashHex.Length; i += 2)
        {
            hashBytes[i / 2] = Convert.ToByte(hashHex.Substring(i, 2), 16);
        }

        var signer = SignerUtilities.GetSigner("SHA256withRSA");
        signer.Init(true, store.GetKey(alias).Key);
        signer.BlockUpdate(hashBytes, 0, hashBytes.Length);
        return BitConverter.ToString(signer.GenerateSignature()).Replace("-", "").ToLowerInvariant();
    }
}
