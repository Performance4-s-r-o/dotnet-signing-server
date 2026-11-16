using System.Text.Json;
using System.Text.Json.Nodes;
using DotNetSigningServer.Services.SignatureDesign;
using iText.IO.Image;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;

namespace DotNetSigningServer.Tests.Services.SignatureDesign;

public class SignatureLayoutRendererTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // 2x1 PNG (red, blue) so contain-fitting has a real aspect ratio.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAAEElEQVR4nGP4z8DAwMDAAAAJ/gH/H0VY8QAAAABJRU5ErkJggg==");

    private static SignatureLayoutImages Images()
    {
        var img = ImageDataFactory.Create(Png);
        return new SignatureLayoutImages { Sig = img, Logo = img, Image = img, Background = img };
    }

    [Fact]
    public void Renders_every_golden_case_and_the_text_is_extractable()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Services", "SignatureDesign", "golden-cases.json");
        var cases = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            foreach (var c in cases)
            {
                var input = c!["input"].Deserialize<SignatureLayoutInput>(Json)!;
                var layout = SignatureLayoutEngine.Compute(input);
                var page = doc.AddNewPage(new PageSize(360, 140));
                var xobject = SignatureLayoutRenderer.Render(doc, layout, Images());
                new PdfCanvas(page).AddXObjectAt(xobject, 20, 20);
            }
        }

        var outDir = Environment.GetEnvironmentVariable("SIGNATURE_LAYOUT_PDF_OUT");
        if (!string.IsNullOrEmpty(outDir)) WriteSheet(cases, System.IO.Path.Combine(outDir, "golden-sheet.pdf"));

        using var read = new PdfDocument(new PdfReader(new MemoryStream(ms.ToArray())));
        Assert.Equal(cases.Count, read.GetNumberOfPages());
        var first = PdfTextExtractor.GetTextFromPage(read.GetPage(1));
        Assert.Contains("Jana Nováková", first);
    }

    /// <summary>All cases on one page, five per row, for a visual check (SIGNATURE_LAYOUT_PDF_OUT).</summary>
    private static void WriteSheet(JsonArray cases, string file)
    {
        using var doc = new PdfDocument(new PdfWriter(file));
        var page = doc.AddNewPage(new PageSize(1500, 1400));
        var canvas = new PdfCanvas(page);
        int i = 0;
        foreach (var c in cases)
        {
            var layout = SignatureLayoutEngine.Compute(c!["input"].Deserialize<SignatureLayoutInput>(Json)!);
            var xobject = SignatureLayoutRenderer.Render(doc, layout, Images());
            float x = 10 + (i % 5) * 300;
            float y = 1400 - 10 - (i / 5 + 1) * 135;
            canvas.SetLineWidth(0.3f).SetStrokeColorGray(0.6f).Rectangle(x - 1, y - 1, (float)layout.Width + 2, (float)layout.Height + 2).Stroke();
            canvas.AddXObjectAt(xobject, x, y);
            i++;
        }
    }

    [Fact]
    public void Pattern_primitives_match_the_portal_counts()
    {
        Assert.Equal(51 + 18, SignatureLayoutRenderer.PatternPrimitives("grid", 4, 200, 70).Lines.Count);
        Assert.Equal(50 * 17, SignatureLayoutRenderer.PatternPrimitives("dots", 4, 200, 70).Dots.Count);
    }
}
