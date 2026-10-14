using System.IO.Compression;
using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using System.Text;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// The limits of #70: what PdfSafetyGuard, ContentLimitGuard and PfxGuard refuse,
/// and that an ordinary request still goes through each of them.
/// </summary>
public class InputLimitsTests
{
    private static PdfSafetyGuard Guard(Action<LimitsOptions>? tweak = null)
    {
        var options = new LimitsOptions();
        tweak?.Invoke(options);
        return new PdfSafetyGuard(TestHelpers.WrapOptions(options));
    }

    private static byte[] Pdf(Action<PdfDocument> configure, PageSize? size = null)
    {
        using var ms = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfWriter(ms)))
        {
            pdf.AddNewPage(size ?? PageSize.A4);
            configure(pdf);
        }
        return ms.ToArray();
    }

    private static string CodeOf(Action act) => Assert.Throws<ApiValidationException>(act).Code;

    // ── PDF ────────────────────────────────────────────────────────────────

    [Fact]
    public void Ordinary_pdf_passes_every_limit()
    {
        Guard().EnsureSafePdf(Pdf(_ => { }), "test");
    }

    [Fact]
    public void A_5_by_5_metre_page_is_refused_before_anything_renders_it()
    {
        // 5 m = 14 173 pt: under the 14 400 pt cap, so a 6 m page is the refused one.
        var pdf = Pdf(_ => { }, new PageSize(17_000, 17_000));
        Assert.Equal("PDF_PAGE_TOO_LARGE", CodeOf(() => Guard().EnsureSafePdf(pdf, "test")));
    }

    [Fact]
    public void UserUnit_scales_the_page_it_describes()
    {
        var pdf = Pdf(d => d.GetPage(1).GetPdfObject().Put(PdfName.UserUnit, new PdfNumber(100)));
        Assert.Equal("PDF_PAGE_TOO_LARGE", CodeOf(() => Guard().EnsureSafePdf(pdf, "test")));
    }

    [Fact]
    public void Too_many_form_fields_are_refused()
    {
        var pdf = Pdf(d =>
        {
            var form = PdfFormCreator.GetAcroForm(d, true);
            for (var i = 0; i < 12; i++)
            {
                form.AddField(new TextFormFieldBuilder(d, $"f{i}").SetWidgetRectangle(new Rectangle(10, 10 + i * 20, 50, 15)).CreateText());
            }
        });
        Assert.Equal("PDF_TOO_COMPLEX", CodeOf(() => Guard(o => o.PdfMaxFormFields = 10).EnsureSafePdf(pdf, "test")));
        Guard(o => o.PdfMaxFormFields = 12).EnsureSafePdf(pdf, "test");
    }

    [Fact]
    public void Too_many_incremental_updates_are_refused()
    {
        var bytes = Pdf(_ => { }).Concat(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("\n%%EOF", 6)))).ToArray();
        Assert.Equal("PDF_TOO_COMPLEX", CodeOf(() => Guard(o => o.PdfMaxIncrementalUpdates = 3).EnsureSafePdf(bytes, "test")));
    }

    [Fact]
    public void A_stream_bomb_is_refused_instead_of_expanding()
    {
        // 8 MB of zeros deflate to a few kilobytes.
        var pdf = Pdf(d =>
        {
            var stream = new PdfStream(new byte[8 * 1024 * 1024]);
            stream.SetCompressionLevel(CompressionConstants.BEST_COMPRESSION);
            d.GetPage(1).GetPdfObject().Put(new PdfName("Bomb"), stream);
        });
        Assert.True(pdf.Length < 100_000);
        Assert.Equal("PDF_TOO_COMPLEX", CodeOf(() => Guard(o => o.PdfMaxStreamBytes = 1024 * 1024).EnsureSafePdf(pdf, "test")));
        Guard().EnsureSafePdf(pdf, "test"); // under the default 64 MB it is an ordinary file
    }

    [Fact]
    public void Inspection_skips_the_active_content_check_but_not_the_limits()
    {
        var pdf = Pdf(d => d.GetCatalog().GetPdfObject().Put(PdfName.OpenAction,
            new PdfDictionary(new Dictionary<PdfName, PdfObject> { [PdfName.S] = PdfName.Launch })));
        Assert.Equal("PDF_ACTIVE_CONTENT", CodeOf(() => Guard().EnsureSafePdf(pdf, "test")));
        Guard().EnsureSafePdf(pdf, "test", checkActiveContent: false);

        var big = Pdf(_ => { }, new PageSize(17_000, 17_000));
        Assert.Equal("PDF_PAGE_TOO_LARGE", CodeOf(() => Guard().EnsureSafePdf(big, "test", checkActiveContent: false)));
    }

    [Fact]
    public void A_file_over_the_byte_limit_is_refused_before_it_is_parsed()
    {
        var pdf = Pdf(_ => { });
        Assert.Equal("PDF_SIZE_EXCEEDED", CodeOf(() => Guard(o => o.PdfMaxBytes = 100).EnsureSafePdf(pdf, "test")));
    }

    // ── Images ─────────────────────────────────────────────────────────────

    private static byte[] Png(int width, int height)
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        b.AddRange(new[] { (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width });
        b.AddRange(new[] { (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height });
        b.AddRange(new byte[] { 8, 6, 0, 0, 0 });
        return b.ToArray();
    }

    private static byte[] Jpeg(int width, int height) => new byte[]
    {
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00,
        0xFF, 0xC0, 0x00, 0x0B, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x01, 0x01, 0x11, 0x00,
    };

    private static ContentLimitGuard Limits() => new(TestHelpers.WrapOptions(new LimitsOptions()));

    [Fact]
    public void Image_dimensions_come_from_the_header()
    {
        Assert.Equal((300, 200), ImageHeader.TryRead(Png(300, 200)));
        Assert.Equal((640, 480), ImageHeader.TryRead(Jpeg(640, 480)));
        Assert.Null(ImageHeader.TryRead("GIF89a"u8.ToArray()));
    }

    [Fact]
    public void A_30000_by_30000_png_is_refused_by_its_header_alone()
    {
        var png = Convert.ToBase64String(Png(30_000, 30_000));
        Assert.Equal("IMAGE_DIMENSIONS_EXCEEDED", CodeOf(() => Limits().EnsureImageWithinLimit(png, "Signature image")));
    }

    [Fact]
    public void An_ordinary_signature_image_passes_and_a_gif_does_not()
    {
        Limits().EnsureImageWithinLimit(Convert.ToBase64String(Png(600, 200)), "Signature image");
        Limits().EnsureImageWithinLimit(Convert.ToBase64String(Jpeg(800, 400)), "Signature image");
        Assert.Equal("IMAGE_FORMAT_UNSUPPORTED",
            CodeOf(() => Limits().EnsureImageWithinLimit(Convert.ToBase64String("GIF89a\0\0\0\0"u8.ToArray()), "Signature image")));
    }

    [Fact]
    public void Base64_with_line_breaks_is_read_like_any_other()
    {
        var flat = Convert.ToBase64String(Png(600, 200));
        var wrapped = string.Join("\r\n", Enumerable.Range(0, (flat.Length + 7) / 8).Select(i => flat.Substring(i * 8, Math.Min(8, flat.Length - i * 8))));
        Assert.Contains('\n', wrapped);
        Limits().EnsureImageWithinLimit(wrapped, "Signature image");
    }

    [Fact]
    public void Fill_images_have_a_looser_limit_than_signatures()
    {
        var photo = Convert.ToBase64String(Png(6_000, 4_000)); // 24 Mpx, over a signature's 4 Mpx
        Assert.Equal("IMAGE_DIMENSIONS_EXCEEDED", CodeOf(() => Limits().EnsureImageWithinLimit(photo, "Logo")));
        Limits().EnsureFillImageWithinLimit(photo);
        Assert.Equal("IMAGE_DIMENSIONS_EXCEEDED",
            CodeOf(() => Limits().EnsureFillImageWithinLimit(Convert.ToBase64String(Png(12_000, 100)))));
    }

    // ── PFX ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_pfx_over_the_size_limit_is_refused()
    {
        Assert.Equal("PFX_TOO_LARGE", CodeOf(() => PfxGuard.EnsureSafe(new byte[PfxGuard.MaxBytes + 1])));
    }

    [Fact]
    public void A_pfx_that_asks_for_100_million_iterations_is_refused_before_loading()
    {
        var cheap = TestPfx(iterations: 2_048);
        PfxGuard.EnsureSafe(cheap); // an ordinary export passes

        var expensive = TestPfx(iterations: 100_000_000, load: false);
        Assert.Equal("PFX_TOO_EXPENSIVE", CodeOf(() => PfxGuard.EnsureSafe(expensive)));
    }

    /// <summary>A PKCS#12 with the given MAC iteration count. <paramref name="load"/>=false only builds the structure.</summary>
    private static byte[] TestPfx(int iterations, bool load = true)
    {
        var rng = new Org.BouncyCastle.Security.SecureRandom();
        var keyGen = new Org.BouncyCastle.Crypto.Generators.RsaKeyPairGenerator();
        keyGen.Init(new Org.BouncyCastle.Crypto.KeyGenerationParameters(rng, 2048));
        var pair = keyGen.GenerateKeyPair();

        var name = new Org.BouncyCastle.Asn1.X509.X509Name("CN=limits-test");
        var certGen = new Org.BouncyCastle.X509.X509V3CertificateGenerator();
        certGen.SetSerialNumber(Org.BouncyCastle.Math.BigInteger.One);
        certGen.SetIssuerDN(name);
        certGen.SetSubjectDN(name);
        certGen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certGen.SetNotAfter(DateTime.UtcNow.AddDays(1));
        certGen.SetPublicKey(pair.Public);
        var cert = certGen.Generate(new Org.BouncyCastle.Crypto.Operators.Asn1SignatureFactory("SHA256WITHRSA", pair.Private, rng));

        var store = new Org.BouncyCastle.Pkcs.Pkcs12StoreBuilder().Build();
        store.SetKeyEntry("k", new Org.BouncyCastle.Pkcs.AsymmetricKeyEntry(pair.Private),
            new[] { new Org.BouncyCastle.Pkcs.X509CertificateEntry(cert) });
        using var ms = new MemoryStream();
        store.Save(ms, "pw".ToCharArray(), rng);
        var bytes = ms.ToArray();
        if (iterations == 2_048 || load) return bytes;

        // Rewrite the MAC's iteration count to the requested value (the MAC is not verified by PfxGuard).
        var pfx = Org.BouncyCastle.Asn1.Pkcs.Pfx.GetInstance(Org.BouncyCastle.Asn1.Asn1Object.FromByteArray(bytes));
        var mac = pfx.MacData;
        var forged = new Org.BouncyCastle.Asn1.Pkcs.MacData(mac.Mac, mac.GetSalt(), iterations);
        return new Org.BouncyCastle.Asn1.Pkcs.Pfx(pfx.AuthSafe, forged).GetEncoded();
    }
}
