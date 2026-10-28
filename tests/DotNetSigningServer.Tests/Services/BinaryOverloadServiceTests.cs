using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Kernel.Pdf;
using iText.Signatures;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// #74: služby mají přetížení přijímající bajty PDF (binární kanál). Verze s base64 volá totéž
/// s dekódovanými bajty, takže obě musí dát stejný výsledek — a přetížení s bajty nesmí číst
/// <c>input.PdfContent</c> (v binárním kanálu je prázdný). Bez Dockeru, nad skutečným iTextem.
/// </summary>
public class BinaryOverloadServiceTests
{
    private static readonly SignRect Rect = new() { X = 10, Y = 10, Width = 200, Height = 50 };
    private static readonly byte[] Pdf = Convert.FromBase64String(TestHelpers.CreateMinimalPdfBase64());
    private static readonly string PdfBase64 = Convert.ToBase64String(Pdf);

    private static PdfSigningService Signing() => new(
        TestHelpers.WrapOptions(new EvidenceOptions()),
        new PdfVisualSigningService());

    private static (int Pages, int Signatures) Shape(string resultBase64)
    {
        using var ms = new MemoryStream(Convert.FromBase64String(resultBase64));
        using var doc = new PdfDocument(new PdfReader(ms));
        return (doc.GetNumberOfPages(), new SignatureUtil(doc).GetSignatureNames().Count);
    }

    [Fact]
    public void SignWithPfx_bytes_overload_matches_base64_and_ignores_PdfContent()
    {
        var (_, pfx, password) = TestHelpers.CreateTestCertificate();
        PfxSignInput Input(string pdf) => new()
        {
            PdfContent = pdf,
            PfxContent = pfx,
            PfxPassword = password,
            Reason = "r",
            Location = "l",
            SignRect = Rect,
        };

        var viaJson = Signing().SignWithPfx(Input(PdfBase64));
        var viaBytes = Signing().SignWithPfx(Input(string.Empty), Pdf);

        Assert.Equal(Shape(viaJson), Shape(viaBytes));
        Assert.Single(PdfSignatureInspector.Inspect(Convert.FromBase64String(viaBytes)).Signatures);
    }

    [Fact]
    public void ApplyDocumentTimestamp_bytes_overload_matches_base64_and_ignores_PdfContent()
    {
        DocumentTimestampInput Input(string pdf) => new() { PdfContent = pdf, SignRect = Rect, SignPageNumber = 1, Visible = false };

        var viaJson = Signing().ApplyDocumentTimestamp(Input(PdfBase64), new LocalTsaClient());
        var viaBytes = Signing().ApplyDocumentTimestamp(Input(string.Empty), new LocalTsaClient(), Pdf);

        Assert.Equal(Shape(viaJson), Shape(viaBytes));
        var stamped = PdfSignatureInspector.Inspect(Convert.FromBase64String(viaBytes));
        Assert.True(Assert.Single(stamped.Signatures).IsDocumentTimestamp);
    }

    [Fact]
    public void ApplyDocumentTimestamp_bytes_overload_still_requires_a_TSA()
    {
        var ex = Assert.Throws<DotNetSigningServer.Exceptions.ApiValidationException>(() =>
            Signing().ApplyDocumentTimestamp(new DocumentTimestampInput { SignRect = Rect }, Pdf));
        Assert.Equal("TSA_NOT_CONFIGURED", ex.Code);
    }

    [Fact]
    public void ApplyVisualSign_bytes_overload_matches_base64_and_ignores_PdfContent()
    {
        VisualSignInput Input(string pdf) => new() { PdfContent = pdf, SignRect = Rect, Reason = "r", Location = "l" };

        var viaJson = Signing().ApplyVisualSign(Input(PdfBase64));
        var viaBytes = Signing().ApplyVisualSign(Input(string.Empty), Pdf);

        Assert.Equal(Shape(viaJson), Shape(viaBytes));
    }

    [Fact]
    public void AddAttachment_bytes_overload_matches_base64_and_ignores_PdfContent()
    {
        var attachment = System.Text.Encoding.UTF8.GetBytes("příloha");
        AddAttachmentInput Input(string pdf) => new()
        {
            PdfContent = pdf,
            AttachmentContent = Convert.ToBase64String(attachment),
            FileName = "a.txt",
            MimeType = "text/plain",
        };

        var viaJson = Signing().AddAttachment(Input(PdfBase64));
        var viaBytes = Signing().AddAttachment(Input(string.Empty), Pdf);

        Assert.Equal(Embedded(viaJson), Embedded(viaBytes));
        Assert.Equal(attachment, Embedded(viaBytes));
    }

    private static byte[] Embedded(string resultBase64)
    {
        using var ms = new MemoryStream(Convert.FromBase64String(resultBase64));
        using var doc = new PdfDocument(new PdfReader(ms));
        var entry = doc.GetCatalog().GetNameTree(PdfName.EmbeddedFiles).GetNames()[new PdfString("a.txt")];
        return ((PdfDictionary)entry).GetAsDictionary(PdfName.EF)!.GetAsStream(PdfName.F).GetBytes();
    }

    [Fact]
    public void ConvertToPdfA_bytes_overload_matches_base64_and_ignores_PdfContent()
    {
        var service = new PdfConversionService(NullLogger<PdfConversionService>.Instance);

        var viaJson = service.ConvertToPdfA(new ConvertToPdfAInput { PdfContent = PdfBase64, Conformance = "PDF/A-2B" });
        var viaBytes = service.ConvertToPdfA(new ConvertToPdfAInput { Conformance = "PDF/A-2B" }, Pdf);

        Assert.Equal(Shape(viaJson), Shape(viaBytes));
    }
}
