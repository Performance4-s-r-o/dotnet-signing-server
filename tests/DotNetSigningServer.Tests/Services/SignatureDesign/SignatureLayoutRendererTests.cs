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

    // 2x1 PNG with an alpha channel (colour type 6), like most signature and logo images.
    private static readonly byte[] RgbaPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAADklEQVR4nGP4z8AAQg0AD3oDfnfpf5cAAAAASUVORK5CYII=");

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
                var input = c!["input"].Deserialize<SignatureLayoutInput>(SignatureLayoutModel.JsonOptions)!;
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
            var layout = SignatureLayoutEngine.Compute(c!["input"].Deserialize<SignatureLayoutInput>(SignatureLayoutModel.JsonOptions)!);
            var xobject = SignatureLayoutRenderer.Render(doc, layout, Images());
            float x = 10 + (i % 5) * 300;
            float y = 1400 - 10 - (i / 5 + 1) * 135;
            canvas.SetLineWidth(0.3f).SetStrokeColorGray(0.6f).Rectangle(x - 1, y - 1, (float)layout.Width + 2, (float)layout.Height + 2).Stroke();
            canvas.AddXObjectAt(xobject, x, y);
            i++;
        }
    }

    private static SignatureLayoutResult Layout(Action<SignatureLayoutModel>? patch = null)
    {
        var model = new SignatureLayoutModel();
        patch?.Invoke(model);
        return SignatureLayoutEngine.Compute(new SignatureLayoutInput
        {
            Model = model,
            Width = 260,
            Height = 90,
            Values = new SignatureLayoutValues { Name = "Jana Nováková", Company = "Performance4 s.r.o.", Date = "12. 11. 2026 14:32" },
            Assets = new SignatureLayoutAssets { Sig = true, Logo = true, Image = true, BackgroundImage = true },
        });
    }

    [Fact]
    public void Pdfa1_refuses_a_transparent_background_but_draws_a_pattern()
    {
        using var doc = new PdfDocument(new PdfWriter(new MemoryStream()));
        var ex = Assert.Throws<SignatureAppearanceUnsupportedException>(() =>
            SignatureLayoutRenderer.Render(doc, Layout(m => m.Background.Kind = "watermark"), Images(), transparencyAllowed: false));
        Assert.Equal(SignatureLayoutRenderer.TransparencyNotAllowed, ex.Code);
        SignatureLayoutRenderer.Render(doc, Layout(m => { m.Background.Kind = "pattern"; m.Elements.Reason.Enabled = true; }), Images(), transparencyAllowed: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Renders_into_pdfa(bool pdfa1)
    {
        var icc = File.OpenRead(System.IO.Path.Combine(AppContext.BaseDirectory, "Resources", "srgb.icc"));
        using var ms = new MemoryStream();
        var conformance = pdfa1 ? iText.Kernel.Pdf.PdfAConformance.PDF_A_1B : iText.Kernel.Pdf.PdfAConformance.PDF_A_2B;
        using (var doc = new iText.Pdfa.PdfADocument(new PdfWriter(ms), conformance,
                   new PdfOutputIntent("Custom", "", "http://www.color.org", "sRGB IEC61966-2.1", icc)))
        {
            var page = doc.AddNewPage(new PageSize(300, 120));
            var layout = Layout(m => { m.Background.Kind = "pattern"; m.Elements.Reason.Enabled = true; });
            var images = Images();
            new PdfCanvas(page).AddXObjectAt(SignatureLayoutRenderer.Render(doc, layout, images, transparencyAllowed: !pdfa1), 10, 10);
        }
        Assert.True(ms.ToArray().Length > 0);
    }

    [Fact]
    public void Pdfa1_refuses_an_image_with_alpha_before_anything_is_written()
    {
        using var doc = new PdfDocument(new PdfWriter(new MemoryStream()));
        var rgba = ImageDataFactory.Create(RgbaPng);
        var images = new SignatureLayoutImages { Sig = rgba };
        var ex = Assert.Throws<SignatureAppearanceUnsupportedException>(() =>
            SignatureLayoutRenderer.Render(doc, Layout(), images, transparencyAllowed: false));
        Assert.Equal(SignatureLayoutRenderer.TransparencyNotAllowed, ex.Code);
        SignatureLayoutRenderer.Render(doc, Layout(), images, transparencyAllowed: true);
    }

    [Fact]
    public void Muted_text_mixes_with_a_solid_background()
    {
        Assert.Equal("#1b3a8c", SignatureLayoutRenderer.MutedBase(new LayoutBackground { Kind = "color", Color = "#1b3a8c" }));
        Assert.Equal("#ffffff", SignatureLayoutRenderer.MutedBase(new LayoutBackground { Kind = "image" }));
    }

    [Fact]
    public void Each_image_is_embedded_once_per_document()
    {
        using var ms = new MemoryStream();
        var images = Images();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(new PageSize(600, 300));
            var layout = Layout(m => { m.Right = "image"; m.Background.Kind = "image"; m.Background.ImageFit = "repeat"; });
            new PdfCanvas(page).AddXObjectAt(SignatureLayoutRenderer.Render(doc, layout, images), 10, 10);
            new PdfCanvas(page).AddXObjectAt(SignatureLayoutRenderer.Render(doc, layout, images), 10, 150);
        }
        using var read = new PdfDocument(new PdfReader(new MemoryStream(ms.ToArray())));
        int imageXObjects = 0;
        for (int i = 1; i <= read.GetNumberOfPdfObjects(); i++)
        {
            if (read.GetPdfObject(i) is PdfStream stream && PdfName.Image.Equals(stream.GetAsName(PdfName.Subtype)))
                imageXObjects++;
        }
        // The test PNG has no alpha: one image object for every use in both appearances.
        Assert.Equal(1, imageXObjects);
    }

    [Fact]
    public void Mixes_colours_like_the_portal()
    {
        Assert.Equal("#4d4d4d", SignatureLayoutRenderer.Mix("#000000", "#ffffff", 0.7));
        Assert.Equal("#1b3a8c", SignatureLayoutRenderer.Mix("#1b3a8c", "#ffffff", 1));
        Assert.Equal("#4d4d4d", SignatureLayoutRenderer.Mix("not-a-colour", "#ffffff", 0.7));
    }

    [Fact]
    public void Pattern_primitives_match_the_portal_counts()
    {
        Assert.Equal(51 + 18, SignatureLayoutRenderer.PatternPrimitives("grid", 4, 200, 70).Lines.Count);
        Assert.Equal(50 * 17, SignatureLayoutRenderer.PatternPrimitives("dots", 4, 200, 70).Dots.Count);
    }
}
