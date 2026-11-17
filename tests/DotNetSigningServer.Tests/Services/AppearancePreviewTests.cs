using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using DotNetSigningServer.Services.SignatureDesign;
using DotNetSigningServer.Services.SignatureLayout;

namespace DotNetSigningServer.Tests.Services;

/// <summary>#117: the appearance drawn on its page, alone and watermarked, nothing signed.</summary>
public class AppearancePreviewTests
{
    private static byte[] ThreePages()
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            for (int i = 0; i < 3; i++) doc.AddNewPage(PageSize.A4);
        }
        return ms.ToArray();
    }

    private static VisualSignInput Input(int page) => new()
    {
        SignPageNumber = page,
        SignRect = new SignRect { X = 300, Y = 80, Width = 200, Height = 70 },
        SignerName = "Petra Malá",
    };

    [Fact]
    public void Only_the_signed_page_comes_back_with_the_stamp_and_the_watermark()
    {
        var result = PdfVisualSigningService.Preview(Input(2), ThreePages(), "cs");

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(result)));
        Assert.Equal(1, doc.GetNumberOfPages());
        var text = PdfTextExtractor.GetTextFromPage(doc.GetPage(1));
        Assert.Contains("Petra Malá", text);
        Assert.Contains("NÁHLED", text);
        // Nothing signed: no form fields at all.
        Assert.Null(doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.AcroForm));
    }

    [Fact]
    public void A_rotated_page_keeps_the_watermark_on_the_page()
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            doc.AddNewPage(PageSize.A4).SetRotation(90);
        }
        var result = PdfVisualSigningService.Preview(Input(1), ms.ToArray(), "xx");

        using var preview = new PdfDocument(new PdfReader(new MemoryStream(result)));
        Assert.Contains("PREVIEW", PdfTextExtractor.GetTextFromPage(preview.GetPage(1)));
    }

    [Fact]
    public void A_pdfa1_document_is_simplified_and_warned_about_as_when_signing()
    {
        CodeScanRaster.EnsureConfigured();
        byte[] pdfa1;
        using (var icc = File.OpenRead(System.IO.Path.Combine(AppContext.BaseDirectory, "Resources", "srgb.icc")))
        using (var ms = new MemoryStream())
        {
            using (var doc = new iText.Pdfa.PdfADocument(new PdfWriter(ms), PdfAConformance.PDF_A_1B,
                       new PdfOutputIntent("Custom", "", "http://www.color.org", "sRGB IEC61966-2.1", icc)))
            {
                doc.AddNewPage(PageSize.A4);
            }
            pdfa1 = ms.ToArray();
        }
        var model = new SignatureLayoutModel();
        model.Background.Kind = "image";
        model.Background.Opacity = 100;
        var input = Input(1);
        input.Appearance = new SignatureAppearanceOptions { Layout = model };
        // 2×1 RGBA PNG with real transparency.
        input.BackgroundImageContent = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAADklEQVR4nGP4z8AAQg0AD3oDfnfpf5cAAAAASUVORK5CYII=";

        var warnings = SignatureAppearance.BeginWarnings();
        PdfVisualSigningService.Preview(input, pdfa1, "cs");
        Assert.Contains(SignatureAppearance.Pdfa1SimplifiedWarning, warnings);
    }

    [Fact]
    public void The_watermark_is_also_inside_the_stamp()
    {
        var result = PdfVisualSigningService.Preview(Input(1), ThreePages(), "en");
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(result)));
        var xobjects = doc.GetPage(1).GetResources().GetResource(PdfName.XObject);
        Assert.NotNull(xobjects);
        var stamp = (PdfStream)xobjects.Values().First(o => o is PdfStream);
        var content = System.Text.Encoding.Latin1.GetString(stamp.GetBytes());
        Assert.Contains("Tj", content);
        Assert.True(content.LastIndexOf("Tj", StringComparison.Ordinal) > content.LastIndexOf("Do", StringComparison.Ordinal) || !content.Contains("Do"));
    }

    [Fact]
    public void A_page_outside_the_document_is_refused()
    {
        var ex = Assert.Throws<DotNetSigningServer.Exceptions.ApiValidationException>(() => PdfVisualSigningService.Preview(Input(4), ThreePages(), "en"));
        Assert.Equal("SIGN_PAGE_INVALID", ex.Code);
    }
}
