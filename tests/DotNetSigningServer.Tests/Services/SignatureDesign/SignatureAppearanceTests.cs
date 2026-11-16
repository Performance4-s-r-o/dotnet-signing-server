using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.SignatureDesign;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;

namespace DotNetSigningServer.Tests.Services.SignatureDesign;

public class SignatureAppearanceTests
{
    // 2x1 PNG with an alpha channel, like a signature drawn on a canvas.
    private const string RgbaPng = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAADklEQVR4nGP4z8AAQg0AD3oDfnfpf5cAAAAASUVORK5CYII=";

    [Fact]
    public void A_seal_with_a_handwritten_signature_or_name_is_refused()
    {
        var withSig = new SignatureAppearanceOptions { Seal = true, Layout = new SignatureLayoutModel() };
        Assert.Equal(SignatureAppearance.SealViolationCode, SignatureAppearance.Refusal(withSig));

        var withName = new SignatureAppearanceOptions { Seal = true, Layout = SignatureAppearance.DefaultSeal() };
        withName.Layout.Elements.Name.Enabled = true;
        Assert.Equal(SignatureAppearance.SealViolationCode, SignatureAppearance.Refusal(withName));

        Assert.Null(SignatureAppearance.Refusal(new SignatureAppearanceOptions { Seal = true }));
        Assert.Null(SignatureAppearance.Refusal(new SignatureAppearanceOptions { Layout = new SignatureLayoutModel() }));
    }

    [Fact]
    public void An_invalid_model_label_or_box_is_refused()
    {
        var model = new SignatureLayoutModel { SplitLeft = 90 };
        Assert.Equal(SignatureAppearance.InvalidCode, SignatureAppearance.Refusal(new SignatureAppearanceOptions { Layout = model }));
        var labels = new SignatureAppearanceOptions { Labels = new SignatureLabels { Reason = "a\nb" } };
        Assert.Equal(SignatureAppearance.InvalidCode, SignatureAppearance.Refusal(labels));

        Assert.Equal(SignatureAppearance.FrameInvalidCode, SignatureAppearance.Refusal(null, new SignRect { Width = 10, Height = 50 }));
        Assert.Equal(SignatureAppearance.FrameInvalidCode, SignatureAppearance.Refusal(null, new SignRect { Width = 200, Height = 1200 }));
        Assert.Null(SignatureAppearance.Refusal(null, new SignRect { Width = 0, Height = 0 }));
        Assert.Equal((200f, 70f), SignatureAppearance.Size(new SignRect()));
    }

    [Fact]
    public void An_unreadable_image_is_refused_not_left_out()
    {
        using var doc = new PdfDocument(new PdfWriter(new MemoryStream()));
        var ex = Assert.Throws<ApiValidationException>(() => SignatureAppearance.Render(doc,
            new SignatureAppearanceContent { SignImage = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }) }, 200, 70));
        Assert.Equal(SignatureAppearance.ImageInvalidCode, ex.Code);
    }

    [Fact]
    public void Pdfa1_flattens_a_drawn_signature_but_refuses_a_watermark()
    {
        // The production ImageMagick policy (read-only except raw grey) must not break flattening.
        DotNetSigningServer.Services.CodeScanRaster.EnsureConfigured();
        var pdf = Pdfa1(doc =>
        {
            var xobject = SignatureAppearance.Render(doc, new SignatureAppearanceContent
            {
                SignImage = RgbaPng,
                Name = "Jana Nováková",
                Company = "Firma s.r.o.",
            }, 200, 70);
            new PdfCanvas(doc.GetFirstPage()).AddXObjectAt(xobject, 10, 10);

            var watermark = new SignatureLayoutModel();
            watermark.Background.Kind = "watermark";
            var ex = Assert.Throws<ApiValidationException>(() => SignatureAppearance.Render(doc, new SignatureAppearanceContent
            {
                Options = new SignatureAppearanceOptions { Layout = watermark },
                LogoImage = RgbaPng,
            }, 200, 70));
            Assert.Equal(SignatureAppearance.TransparencyNotAllowedCode, ex.Code);
        });

        using var read = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var text = PdfTextExtractor.GetTextFromPage(read.GetFirstPage());
        Assert.Contains("Jana Nováková", text);
        Assert.Contains("Firma s.r.o.", text);
    }

    [Fact]
    public void An_image_the_layout_does_not_draw_is_not_decoded()
    {
        using var doc = new PdfDocument(new PdfWriter(new MemoryStream()));
        // The default model draws no stamp: garbage in StampImage cannot fail the signature.
        SignatureAppearance.Render(doc, new SignatureAppearanceContent { StampImage = Convert.ToBase64String(new byte[] { 1, 2, 3 }) }, 200, 70);
    }

    [Fact]
    public void Pdfa1_accepts_a_fully_opaque_background_image()
    {
        var model = new SignatureLayoutModel();
        model.Background.Kind = "image";
        model.Background.Opacity = 100;
        Pdfa1(doc => new PdfCanvas(doc.GetFirstPage()).AddXObjectAt(SignatureAppearance.Render(doc, new SignatureAppearanceContent
        {
            Options = new SignatureAppearanceOptions { Layout = model },
            // 2x1 PNG without alpha.
            BackgroundImage = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAAEElEQVR4nGP4z8DAwMDAAAAJ/gH/H0VY8QAAAABJRU5ErkJggg==",
            Name = "Jana",
        }, 200, 70), 10, 10));
    }

    [Fact]
    public void Pdfa1_refuses_a_drawn_signature_over_a_background_image()
    {
        var model = new SignatureLayoutModel();
        model.Background.Kind = "image";
        model.Background.Opacity = 100;
        Pdfa1(doc =>
        {
            var ex = Assert.Throws<ApiValidationException>(() => SignatureAppearance.Render(doc, new SignatureAppearanceContent
            {
                Options = new SignatureAppearanceOptions { Layout = model },
                SignImage = RgbaPng,
                BackgroundImage = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAAEElEQVR4nGP4z8DAwMDAAAAJ/gH/H0VY8QAAAABJRU5ErkJggg==",
            }, 200, 70));
            Assert.Equal(SignatureAppearance.TransparencyNotAllowedCode, ex.Code);
        });
    }

    [Fact]
    public void A_large_drawn_signature_is_scaled_down_when_flattened()
    {
        DotNetSigningServer.Services.CodeScanRaster.EnsureConfigured();
        // ImageMagick may not write under the policy: the PNG is built by hand.
        var bytes = RgbaPngOf(4000, 1000);
        var (flat, realAlpha) = SignatureAppearance.Opaque(new SignatureAppearance.Decoded(bytes, iText.IO.Image.ImageDataFactory.Create(bytes)), "#ffffff");
        Assert.True(realAlpha);
        Assert.Equal(SignatureAppearance.MaxFlattenSidePx, (int)flat!.Data.GetWidth());
        Assert.Equal(500, (int)flat.Data.GetHeight());
        Assert.Null(flat.Data.GetImageMask());
        // End to end: the scaled image passes the PDF/A-1 check on close.
        Pdfa1(doc => new PdfCanvas(doc.GetFirstPage()).AddXObjectAt(SignatureAppearance.Render(doc,
            new SignatureAppearanceContent { SignImage = Convert.ToBase64String(bytes), Name = "Jana" }, 200, 70), 10, 10));
    }

    [Fact]
    public void Pdfa1_refuses_a_drawn_signature_over_an_opaque_watermark()
    {
        DotNetSigningServer.Services.CodeScanRaster.EnsureConfigured();
        var model = new SignatureLayoutModel();
        model.Background.Kind = "watermark";
        model.Background.Opacity = 100;
        Pdfa1(doc =>
        {
            var ex = Assert.Throws<ApiValidationException>(() => SignatureAppearance.Render(doc, new SignatureAppearanceContent
            {
                Options = new SignatureAppearanceOptions { Layout = model },
                SignImage = RgbaPng,
                LogoImage = Convert.ToBase64String(RgbaPngOf(4, 2, opaque: true)),
            }, 200, 70));
            Assert.Equal(SignatureAppearance.TransparencyNotAllowedCode, ex.Code);
        });
    }

    [Fact]
    public void Pdfa1_refuses_a_background_image_with_real_transparency()
    {
        DotNetSigningServer.Services.CodeScanRaster.EnsureConfigured();
        var model = new SignatureLayoutModel();
        model.Background.Kind = "image";
        model.Background.Opacity = 100;
        Pdfa1(doc =>
        {
            var ex = Assert.Throws<ApiValidationException>(() => SignatureAppearance.Render(doc, new SignatureAppearanceContent
            {
                Options = new SignatureAppearanceOptions { Layout = model },
                BackgroundImage = Convert.ToBase64String(RgbaPngOf(4, 2)),
            }, 200, 70));
            Assert.Equal(SignatureAppearance.TransparencyNotAllowedCode, ex.Code);
        });
    }

    [Fact]
    public void Pdfa1_accepts_an_rgba_logo_without_transparency_over_a_background_image()
    {
        DotNetSigningServer.Services.CodeScanRaster.EnsureConfigured();
        var model = new SignatureLayoutModel { Left = "logo" };
        model.Background.Kind = "image";
        model.Background.Opacity = 100;
        var opaqueRgba = Convert.ToBase64String(RgbaPngOf(4, 2, opaque: true));
        Pdfa1(doc => new PdfCanvas(doc.GetFirstPage()).AddXObjectAt(SignatureAppearance.Render(doc, new SignatureAppearanceContent
        {
            Options = new SignatureAppearanceOptions { Layout = model },
            LogoImage = opaqueRgba,
            BackgroundImage = opaqueRgba,
            Name = "Jana",
        }, 200, 70), 10, 10));
    }

    [Fact]
    public void The_default_seal_has_no_person()
    {
        var seal = SignatureAppearance.ModelFor(new SignatureAppearanceOptions { Seal = true });
        Assert.Null(seal.SealViolation());
        Assert.Null(seal.Validate());
        Assert.Equal("sig", SignatureAppearance.ModelFor(null).Left);
    }

    /// <summary>A transparent RGBA PNG of the given size (one opaque pixel), built without ImageMagick.</summary>
    private static byte[] RgbaPngOf(int width, int height, bool opaque = false)
    {
        var raw = new byte[height * (1 + width * 4)];
        raw[1 + 3] = 255; // first pixel opaque black
        if (opaque)
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++) raw[y * (1 + width * 4) + 1 + x * 4 + 3] = 255;
        using var deflated = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(deflated, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); png.Write(len);
            var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            png.Write(body);
            uint crc = 0xFFFFFFFF;
            foreach (var b in body) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1; }
            var c = BitConverter.GetBytes(~crc); Array.Reverse(c); png.Write(c);
        }
        var ihdr = new byte[13];
        var wb = BitConverter.GetBytes(width); Array.Reverse(wb); wb.CopyTo(ihdr, 0);
        var hb = BitConverter.GetBytes(height); Array.Reverse(hb); hb.CopyTo(ihdr, 4);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk("IHDR", ihdr);
        Chunk("IDAT", deflated.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static byte[] Pdfa1(Action<PdfDocument> draw)
    {
        using var icc = File.OpenRead(System.IO.Path.Combine(AppContext.BaseDirectory, "Resources", "srgb.icc"));
        using var ms = new MemoryStream();
        using (var doc = new iText.Pdfa.PdfADocument(new PdfWriter(ms), PdfAConformance.PDF_A_1B,
                   new PdfOutputIntent("Custom", "", "http://www.color.org", "sRGB IEC61966-2.1", icc)))
        {
            doc.AddNewPage(new PageSize(300, 120));
            draw(doc);
        }
        return ms.ToArray();
    }
}

public class SignatureFieldAppearanceTests
{
    [Fact]
    public void The_certificate_field_draws_the_appearance_edge_to_edge()
    {
        var (_, pfx, password) = DotNetSigningServer.Tests.Helpers.TestHelpers.CreateTestCertificate();
        var signed = new DotNetSigningServer.Services.PdfSigningService().SignWithPfx(new PfxSignInput
        {
            PfxContent = pfx,
            PfxPassword = password,
            SignerName = "Jana Nováková",
            SignRect = new SignRect { X = 100, Y = 400, Width = 260, Height = 90 },
            Appearance = new SignatureAppearanceOptions(),
        }, DotNetSigningServer.Tests.Services.PresignPdfaTests.Pdf(_ => { }));

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(Convert.FromBase64String(signed))));
        var widget = iText.Forms.PdfAcroForm.GetAcroForm(doc, false)!.GetAllFormFields().Values.First().GetWidgets()[0];
        // /N → /FRM → /n2 → the appearance XObject, each at 1 0 0 1 0 0 and the full box.
        var frm = widget.GetNormalAppearanceObject().GetAsDictionary(PdfName.Resources).GetAsDictionary(PdfName.XObject).GetAsStream(new PdfName("FRM"));
        var n2 = frm.GetAsDictionary(PdfName.Resources).GetAsDictionary(PdfName.XObject).GetAsStream(new PdfName("n2"));
        var content = System.Text.Encoding.ASCII.GetString(n2.GetBytes());
        // The XObject is drawn untransformed: identity matrix directly before its Do.
        Assert.Matches(@"1 0 0 1 0 0 cm\s*/\w+ Do", content);
        var inner = n2.GetAsDictionary(PdfName.Resources).GetAsDictionary(PdfName.XObject);
        var appearance = inner.GetAsStream(inner.KeySet().Single());
        Assert.Equal(new[] { 0f, 0f, 260f, 90f }, appearance.GetAsArray(PdfName.BBox).ToFloatArray());
    }
}
