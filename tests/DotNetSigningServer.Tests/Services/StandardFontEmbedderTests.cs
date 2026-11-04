using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Pdfa;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services;

/// <summary>#189: a PDF with non-embedded standard fonts converts to PDF/A; any other non-embedded font still fails.</summary>
public class StandardFontEmbedderTests
{
    private static readonly PdfConversionService Conversion = new(NullLogger<PdfConversionService>.Instance);

    private static byte[] Build(params string[] standardFonts)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            var canvas = new PdfCanvas(page);
            var y = 780f;
            foreach (var name in standardFonts)
            {
                var font = PdfFontFactory.CreateFont(name, PdfFontFactory.EmbeddingStrategy.PREFER_NOT_EMBEDDED);
                canvas.BeginText().SetFontAndSize(font, 14).MoveText(50, y).ShowText("Hello " + name + " 0123").EndText();
                y -= 30;
            }
        }
        return ms.ToArray();
    }

    private static byte[] Convert(byte[] pdf) =>
        System.Convert.FromBase64String(Conversion.ConvertToPdfA(new ConvertToPdfAInput { Conformance = "PDF/A-2B" }, pdf));

    [Fact]
    public void Standard_fonts_convert_to_PDFA_2B_and_validate()
    {
        var pdf = Build(StandardFonts.HELVETICA, StandardFonts.HELVETICA_BOLD, StandardFonts.HELVETICA_OBLIQUE,
            StandardFonts.TIMES_ROMAN, StandardFonts.TIMES_BOLDITALIC, StandardFonts.COURIER, StandardFonts.COURIER_BOLD);

        var converted = Convert(pdf);

        Assert.Empty(PdfAConformanceCheck.Problems(converted, PdfAConformance.PDF_A_2B));
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(converted)));
        var text = PdfTextExtractor.GetTextFromPage(doc.GetPage(1));
        Assert.Contains("Hello Helvetica-Bold 0123", text);
        Assert.Contains("Hello Times-BoldItalic 0123", text);
        Assert.Contains("Hello Courier 0123", text);
    }

    [Fact]
    public void Widths_stay_those_of_the_standard_font()
    {
        var pdf = Build(StandardFonts.HELVETICA);
        var converted = Convert(pdf);
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(converted)));
        var fonts = doc.GetPage(1).GetResources().GetResource(PdfName.Font);
        var font = fonts.GetAsDictionary(fonts.KeySet().First());
        var widths = font.GetAsArray(PdfName.Widths);
        var first = font.GetAsNumber(PdfName.FirstChar).IntValue();
        // 'A' is 667/1000 in Helvetica, 'i' 222, ' ' 278; Liberation rounds from 2048 units, so within 1.
        Assert.InRange(widths.GetAsNumber('A' - first).IntValue(), 666, 668);
        Assert.InRange(widths.GetAsNumber('i' - first).IntValue(), 221, 223);
        Assert.InRange(widths.GetAsNumber(' ' - first).IntValue(), 277, 279);
    }

    [Fact]
    public void Document_without_standard_fonts_is_untouched()
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms))) doc.AddNewPage();
        var pdf = ms.ToArray();
        Assert.Same(pdf, StandardFontEmbedder.Embed(pdf));
    }

    [Fact]
    public void Other_non_embedded_font_is_PDFA_CONVERSION_FAILED()
    {
        var pdf = Build(StandardFonts.HELVETICA, StandardFonts.ZAPFDINGBATS);
        var limits = new DotNetSigningServer.Options.LimitsOptions { RejectActiveContent = true };
        var guard = new PdfSafetyGuard(DotNetSigningServer.Tests.Helpers.TestHelpers.WrapOptions(limits));

        var ex = Assert.Throws<ApiValidationException>(() => DocumentPreparation.Prepare(pdf,
            new VisualSignInput { ConvertToPdfa = true, PdfaConformance = "PDF/A-2B" }, limits, guard, Conversion, "test"));

        Assert.Equal("PDFA_CONVERSION_FAILED", ex.Code);
    }
}
