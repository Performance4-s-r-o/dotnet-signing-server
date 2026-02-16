using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Kernel.Pdf;

namespace DotNetSigningServer.Tests.Services;

public class PdfSigningServiceTests : IDisposable
{
    private readonly PdfSigningService _sut;
    private readonly List<string> _tempFiles = new();

    public PdfSigningServiceTests()
    {
        _sut = new PdfSigningService(TestHelpers.WrapOptions(new TimestampAuthorityOptions()));
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { }
        }
    }

    [Fact]
    public void SignWithPfx_ProducesValidPdf()
    {
        var pdfBase64 = TestHelpers.CreateMinimalPdfBase64();
        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();

        var result = _sut.SignWithPfx(new PfxSignInput
        {
            PdfContent = pdfBase64,
            PfxContent = pfxBase64,
            PfxPassword = password,
            Location = "Test",
            Reason = "Unit Test",
            SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 }
        });

        Assert.False(string.IsNullOrWhiteSpace(result));
        // Verify the output is valid base64 and a valid PDF
        var bytes = Convert.FromBase64String(result);
        Assert.True(bytes.Length > 0);
        using var ms = new MemoryStream(bytes);
        using var reader = new PdfReader(ms);
        using var pdfDoc = new PdfDocument(reader);
        Assert.True(pdfDoc.GetNumberOfPages() >= 1);
    }

    [Fact]
    public void SignWithPfx_OutputDiffersFromInput()
    {
        var pdfBase64 = TestHelpers.CreateMinimalPdfBase64();
        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();

        var result = _sut.SignWithPfx(new PfxSignInput
        {
            PdfContent = pdfBase64,
            PfxContent = pfxBase64,
            PfxPassword = password,
            Location = "Test",
            Reason = "Unit Test",
            SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 }
        });

        Assert.NotEqual(pdfBase64, result);
    }

    [Fact]
    public void HandlePreSign_ReturnsPathAndHash()
    {
        var pdfBase64 = TestHelpers.CreateMinimalPdfBase64();
        var (certPem, _, _) = TestHelpers.CreateTestCertificate();

        var (path, hash) = _sut.HandlePreSign(new PreSignInput
        {
            PdfContent = pdfBase64,
            CertificatePem = certPem,
            Location = "Test",
            Reason = "Unit Test",
            SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 },
            SignPageNumber = 1
        }, "Signature1");

        _tempFiles.Add(path);

        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(File.Exists(path));
        Assert.False(string.IsNullOrWhiteSpace(hash));
        // Hash should be lowercase hex
        Assert.Matches("^[0-9a-f]+$", hash);
    }

    [Fact]
    public void HandlePreSign_ThenHandleSign_ProducesSignedPdf()
    {
        var pdfBase64 = TestHelpers.CreateMinimalPdfBase64();
        var (certPem, pfxBase64, password) = TestHelpers.CreateTestCertificate();

        // Step 1: Presign
        var (path, hashHex) = _sut.HandlePreSign(new PreSignInput
        {
            PdfContent = pdfBase64,
            CertificatePem = certPem,
            Location = "Test",
            Reason = "Presign-Sign Test",
            SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 },
            SignPageNumber = 1
        }, "Signature1");
        _tempFiles.Add(path);

        // Step 2: Sign the hash with the private key
        // Load private key from PFX
        var pfxBytes = Convert.FromBase64String(pfxBase64);
        using var pfxMs = new MemoryStream(pfxBytes);
        var store = new Org.BouncyCastle.Pkcs.Pkcs12StoreBuilder().Build();
        store.Load(pfxMs, password.ToCharArray());
        var alias = store.Aliases.Cast<string>().First(store.IsKeyEntry);
        var privateKey = store.GetKey(alias).Key;

        // Sign the authenticated attributes hash
        var hashBytes = HexStringToByteArray(hashHex);
        var signer = Org.BouncyCastle.Security.SignerUtilities.GetSigner("SHA256withRSA");
        signer.Init(true, privateKey);
        signer.BlockUpdate(hashBytes, 0, hashBytes.Length);
        var signatureBytes = signer.GenerateSignature();
        var signedHashHex = BitConverter.ToString(signatureBytes).Replace("-", "").ToLowerInvariant();

        // Step 3: Complete signing
        var signedPdfBase64 = _sut.HandleSign(
            new SignInput { Id = "test", SignedHash = signedHashHex },
            path,
            certPem,
            "Signature1");

        Assert.False(string.IsNullOrWhiteSpace(signedPdfBase64));
        var signedBytes = Convert.FromBase64String(signedPdfBase64);
        using var ms = new MemoryStream(signedBytes);
        using var reader = new PdfReader(ms);
        using var doc = new PdfDocument(reader);
        Assert.True(doc.GetNumberOfPages() >= 1);
    }

    [Fact]
    public void HandleSign_MissingPresignedFile_Throws()
    {
        var (certPem, _, _) = TestHelpers.CreateTestCertificate();
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.pdf");

        Assert.Throws<FileNotFoundException>(() =>
            _sut.HandleSign(
                new SignInput { Id = "test", SignedHash = "aabbccdd" },
                nonExistentPath,
                certPem,
                "Signature1"));
    }

    [Fact]
    public void AddAttachment_ProducesValidPdf()
    {
        var pdfBase64 = TestHelpers.CreateMinimalPdfBase64();
        var attachmentContent = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Hello, World!"));

        var result = _sut.AddAttachment(new AddAttachmentInput
        {
            PdfContent = pdfBase64,
            AttachmentContent = attachmentContent,
            FileName = "test.txt",
            Description = "Test attachment",
            MimeType = "text/plain"
        });

        Assert.False(string.IsNullOrWhiteSpace(result));
        var bytes = Convert.FromBase64String(result);
        using var ms = new MemoryStream(bytes);
        using var reader = new PdfReader(ms);
        using var doc = new PdfDocument(reader);
        Assert.True(doc.GetNumberOfPages() >= 1);
    }

    [Fact]
    public void AddAttachment_EmptyFileName_Throws()
    {
        var pdfBase64 = TestHelpers.CreateMinimalPdfBase64();
        var attachmentContent = Convert.ToBase64String(new byte[] { 1, 2, 3 });

        Assert.Throws<ArgumentException>(() =>
            _sut.AddAttachment(new AddAttachmentInput
            {
                PdfContent = pdfBase64,
                AttachmentContent = attachmentContent,
                FileName = "",
                Description = "Test"
            }));
    }

    [Fact]
    public void AddAttachment_WithoutMimeType_ProducesValidPdf()
    {
        var pdfBase64 = TestHelpers.CreateMinimalPdfBase64();
        var attachmentContent = Convert.ToBase64String(new byte[] { 1, 2, 3 });

        var result = _sut.AddAttachment(new AddAttachmentInput
        {
            PdfContent = pdfBase64,
            AttachmentContent = attachmentContent,
            FileName = "data.bin"
        });

        Assert.False(string.IsNullOrWhiteSpace(result));
        var bytes = Convert.FromBase64String(result);
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void ApplyDocumentTimestamp_NoTsa_Throws()
    {
        var pdfBase64 = TestHelpers.CreateMinimalPdfBase64();

        Assert.Throws<InvalidOperationException>(() =>
            _sut.ApplyDocumentTimestamp(new DocumentTimestampInput
            {
                PdfContent = pdfBase64,
                Location = "Test",
                Reason = "Test"
            }));
    }

    private static byte[] HexStringToByteArray(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < hex.Length; i += 2)
        {
            bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
        }
        return bytes;
    }
}
