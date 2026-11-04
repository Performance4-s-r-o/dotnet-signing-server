using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.SignatureLayout;
using DotNetSigningServer.Tests.Helpers;
using iText.Forms;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Signatures;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Pkcs;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// Portál #511: image signature as a content stamp in presign, seal field placed beside it,
/// both in one presign covered by the one signature.
/// </summary>
public class ContentStampPresignTests : IDisposable
{
    private readonly List<string> _files = new();
    private static readonly Rectangle A4 = PageSize.A4;

    public void Dispose()
    {
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch { }
        }
    }

    // ── placement ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_seal_goes_right_of_the_stamp_top_aligned()
    {
        var stamp = new Rectangle(50, 100, 200, 60);
        var seal = SealPlacement.Beside(stamp, 150, 50, A4, Array.Empty<Rectangle>());
        Assert.Equal(256, seal.GetX(), 2);
        Assert.Equal(stamp.GetTop(), seal.GetTop(), 2);
    }

    [Fact]
    public void Near_the_right_edge_the_seal_goes_left()
    {
        var stamp = new Rectangle(A4.GetWidth() - 210, 100, 200, 60);
        var seal = SealPlacement.Beside(stamp, 150, 50, A4, Array.Empty<Rectangle>());
        Assert.Equal(stamp.GetLeft() - SealPlacement.Gap - 150, seal.GetX(), 2);
    }

    [Fact]
    public void A_full_width_stamp_gets_the_seal_below_and_at_the_bottom_above()
    {
        var wide = new Rectangle(10, 300, A4.GetWidth() - 20, 60);
        Assert.Equal(300 - SealPlacement.Gap - 50, SealPlacement.Beside(wide, 150, 50, A4, Array.Empty<Rectangle>()).GetY(), 2);

        var bottom = new Rectangle(10, 5, A4.GetWidth() - 20, 40);
        Assert.Equal(45 + SealPlacement.Gap, SealPlacement.Beside(bottom, 150, 50, A4, Array.Empty<Rectangle>()).GetY(), 2);
    }

    [Fact]
    public void Another_signer_s_field_is_avoided()
    {
        var stamp = new Rectangle(50, 400, 200, 60);
        var earlier = new Rectangle(256, 400, 150, 60);
        var seal = SealPlacement.Beside(stamp, 150, 50, A4, new[] { earlier });
        Assert.False(Overlaps(seal, earlier));
        Assert.False(Overlaps(seal, stamp));
        Assert.True(Inside(seal, A4));
    }

    [Fact]
    public void With_no_free_spot_the_seal_still_stays_on_the_page_and_off_the_stamp()
    {
        var stamp = new Rectangle(50, 400, 200, 60);
        var everywhere = new[] { new Rectangle(0, 0, A4.GetWidth(), A4.GetHeight()) };
        var seal = SealPlacement.Beside(stamp, 150, 50, A4, everywhere);
        Assert.True(Inside(seal, A4));
        Assert.False(Overlaps(seal, stamp));
    }

    // ── same renderer as the certificate field ─────────────────────────────────────────────────

    [Fact]
    public void The_stamp_lands_in_the_same_box_as_a_certificate_field_with_the_same_input()
    {
        var input = Stamp();
        var (_, _, rect) = PdfVisualSigningService.StampContent(input, Blank(), keepPdfA: null);
        var props = PdfVisualSigningService.BuildSignerProperties(
            "f", input.SignRect, input.Reason, input.Location, 1, input.SignImageContent, null, input.Appearance,
            designWidth: input.DesignWidth, designHeight: input.DesignHeight, autoHeight: input.AutoHeight,
            pageSize: A4, signerNameOverride: input.SignerName);
        var field = props.GetPageRect();
        Assert.Equal(field.GetX(), rect.GetX(), 2);
        Assert.Equal(field.GetY(), rect.GetY(), 2);
        Assert.Equal(field.GetWidth(), rect.GetWidth(), 2);
        Assert.Equal(field.GetHeight(), rect.GetHeight(), 2);
    }

    // ── presign ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stamp_and_seal_in_one_presign_are_covered_by_the_signature()
    {
        var (signed, placement) = PresignAndSign(Blank(), keep: null);

        Assert.NotNull(placement);
        Assert.Equal(1, placement!.Page);
        Assert.False(Overlaps(ToRect(placement.Field), ToRect(placement.Stamp)));
        Assert.True(Inside(ToRect(placement.Field), A4));

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(signed)));
        var util = new SignatureUtil(doc);
        Assert.True(util.SignatureCoversWholeDocument("Seal1"));
        Assert.True(util.ReadSignatureData("Seal1").VerifySignatureIntegrityAndAuthenticity());
        // The stamp is page content, the seal its own field.
        Assert.Contains("Jana Nováková", PdfTextExtractor.GetTextFromPage(doc.GetPage(1)));
        var widget = PdfAcroForm.GetAcroForm(doc, false)!.GetField("Seal1").GetWidgets()[0].GetRectangle().ToRectangle();
        Assert.Equal(placement.Field.X, widget.GetX(), 1);
        Assert.Equal(placement.Field.Y, widget.GetY(), 1);
    }

    [Fact]
    public void A_second_signer_s_seal_avoids_the_first_one()
    {
        var (first, p1) = PresignAndSign(Blank(), keep: null);
        var (_, p2) = PresignAndSign(first, keep: null, fieldName: "Seal2");
        Assert.False(Overlaps(ToRect(p2!.Field), ToRect(p1!.Field)));
    }

    [Fact]
    public void Stamp_and_seal_keep_a_PDFA3B_document()
    {
        var limits = new LimitsOptions { RejectActiveContent = true };
        var prepared = DocumentPreparation.Prepare(Blank(),
            new PreSignInput { ConvertToPdfa = true, PdfaConformance = "PDF/A-3B" },
            limits, new PdfSafetyGuard(TestHelpers.WrapOptions(limits)), new PdfConversionService(NullLogger<PdfConversionService>.Instance));

        var (signed, _) = PresignAndSign(prepared.Pdf, prepared.KeepPdfA);

        Assert.Empty(PdfAConformanceCheck.Problems(signed, PdfAConformance.PDF_A_3B));
    }

    [Fact]
    public void Placing_beside_without_a_stamp_is_refused()
    {
        var (certPem, _, _) = TestHelpers.CreateTestCertificate();
        var ex = Assert.Throws<ApiValidationException>(() => new PdfSigningService().HandlePreSignWithStamp(
            new PreSignInput { CertificatePem = certPem, PlaceBesideContentStamp = true }, Blank(), "Seal1"));
        Assert.Equal("CONTENT_STAMP_REQUIRED", ex.Code);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private (byte[] Signed, ContentStampPlacement? Placement) PresignAndSign(byte[] pdf, PdfAConformance? keep, string fieldName = "Seal1")
    {
        var (certPem, pfx, password) = TestHelpers.CreateTestCertificate();
        var input = new PreSignInput
        {
            CertificatePem = certPem,
            SignRect = new SignRect { Width = 150, Height = 50 },
            Appearance = new SignatureAppearanceOptions { DescriptionText = "Electronic seal", ShowSignerName = false, ShowReason = false, ShowLocation = false },
            ContentStamp = Stamp(),
            PlaceBesideContentStamp = true,
        };
        var (path, hash, placement) = new PdfSigningService().HandlePreSignWithStamp(input, pdf, fieldName, keep);
        _files.Add(path);
        var signed = Convert.FromBase64String(new PdfSigningService().HandleSign(
            new SignInput { Id = "x", SignedHash = SignHash(hash, pfx, password) }, path, certPem, fieldName));
        return (signed, placement);
    }

    private static ContentStampInput Stamp() => new()
    {
        SignRect = new SignRect { X = 50, Y = 100, Width = 200, Height = 60 },
        SignPageNumber = 1,
        Reason = "Souhlas",
        Location = "Praha",
        SignerName = "Jana Nováková",
        Appearance = new SignatureAppearanceOptions { FontFamily = "Helvetica" },
        DesignWidth = 200,
        DesignHeight = 60,
    };

    private static byte[] Blank() => PresignPdfaTests.Pdf(_ => { });

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

    private static Rectangle ToRect(SignRect r) => new(r.X, r.Y, r.Width, r.Height);

    private static bool Overlaps(Rectangle a, Rectangle b) =>
        a.GetLeft() < b.GetRight() && b.GetLeft() < a.GetRight() && a.GetBottom() < b.GetTop() && b.GetBottom() < a.GetTop();

    private static bool Inside(Rectangle r, Rectangle page) =>
        r.GetLeft() >= 0 && r.GetBottom() >= 0 && r.GetRight() <= page.GetWidth() + 0.01f && r.GetTop() <= page.GetHeight() + 0.01f;
}
