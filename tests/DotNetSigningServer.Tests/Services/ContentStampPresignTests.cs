using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.SignatureLayout;
using DotNetSigningServer.Tests.Helpers;
using iText.Forms;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
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
        var seal = SealPlacement.Beside(stamp, 150, 50, A4, new[] { earlier })!;
        Assert.False(Overlaps(seal, earlier));
        Assert.False(Overlaps(seal, stamp));
        Assert.True(Inside(seal, A4));
    }

    [Fact]
    public void With_no_free_spot_there_is_no_seal_spot()
    {
        var stamp = new Rectangle(50, 400, 200, 60);
        var everywhere = new[] { new Rectangle(0, 0, A4.GetWidth(), A4.GetHeight()) };
        Assert.Null(SealPlacement.Beside(stamp, 150, 50, A4, everywhere));
    }

    [Fact]
    public void A_stamp_filling_a_small_page_leaves_no_seal_spot()
    {
        var page = new Rectangle(0, 0, 220, 80);
        Assert.Null(SealPlacement.Beside(new Rectangle(10, 10, 200, 60), 150, 50, page, Array.Empty<Rectangle>()));
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
        var (_, p2) = PresignAndSign(first, keep: null, fieldName: "Seal2", stamp: Stamp(y: 300));
        Assert.False(Overlaps(ToRect(p2!.Field), ToRect(p1!.Field)));
        Assert.False(Overlaps(ToRect(p2.Stamp), ToRect(p1.Stamp)));
        Assert.False(Overlaps(ToRect(p2.Field), ToRect(p1.Stamp)));
    }

    // ── a document that is already signed (review: stamp must not change the page) ─────────────

    [Fact]
    public void A_second_signer_leaves_the_page_content_of_the_first_revision_and_its_signature_intact()
    {
        var (first, p1) = PresignAndSign(Blank(), keep: null);
        Assert.False(p1!.Annotation);
        var (second, p2) = PresignAndSign(first, keep: null, fieldName: "Seal2", stamp: Stamp(y: 300));
        Assert.True(p2!.Annotation);

        // The first revision is a byte-for-byte prefix and its page content did not change.
        Assert.Equal(first, second.Take(first.Length).ToArray());
        byte[] ContentOf(byte[] pdf)
        {
            using var d = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
            return d.GetPage(1).GetContentBytes();
        }
        Assert.Equal(ContentOf(first), ContentOf(second));

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(second)));
        var util = new SignatureUtil(doc);
        Assert.True(util.ReadSignatureData("Seal1").VerifySignatureIntegrityAndAuthenticity());
        Assert.True(util.ReadSignatureData("Seal2").VerifySignatureIntegrityAndAuthenticity());
        Assert.True(util.SignatureCoversWholeDocument("Seal2"));
        Assert.Equal(first.Length, util.GetSignature("Seal1").GetByteRange().GetAsNumber(2).IntValue()
            + util.GetSignature("Seal1").GetByteRange().GetAsNumber(3).IntValue());

        // The stamp is a locked, printable stamp annotation with its own appearance, in Seal2's revision.
        var stamp = doc.GetPage(1).GetAnnotations().OfType<iText.Kernel.Pdf.Annot.PdfStampAnnotation>().Single();
        Assert.StartsWith(SealPlacement.StampAnnotationPrefix, stamp.GetName().ToUnicodeString());
        Assert.NotNull(stamp.GetNormalAppearanceObject());
        var flags = stamp.GetFlags();
        Assert.Equal(iText.Kernel.Pdf.Annot.PdfAnnotation.PRINT | iText.Kernel.Pdf.Annot.PdfAnnotation.READ_ONLY | iText.Kernel.Pdf.Annot.PdfAnnotation.LOCKED, flags);
        using var rev2 = new PdfDocument(new PdfReader(util.ExtractRevision("Seal2")));
        Assert.Single(rev2.GetPage(1).GetAnnotations().OfType<iText.Kernel.Pdf.Annot.PdfStampAnnotation>());
    }

    [Fact]
    public void A_stamp_over_an_earlier_content_stamp_is_refused()
    {
        var (first, _) = PresignAndSign(Blank(), keep: null);
        var ex = Assert.Throws<ApiValidationException>(() => PresignAndSign(first, keep: null, fieldName: "Seal2"));
        Assert.Equal(SealPlacement.NoRoomCode, ex.Code);
    }

    [Fact]
    public void A_stamp_over_an_earlier_stamp_annotation_is_refused()
    {
        var (first, _) = PresignAndSign(Blank(), keep: null);
        var (second, _) = PresignAndSign(first, keep: null, fieldName: "Seal2", stamp: Stamp(y: 300));
        var ex = Assert.Throws<ApiValidationException>(() => PresignAndSign(second, keep: null, fieldName: "Seal3", stamp: Stamp(y: 300)));
        Assert.Equal(SealPlacement.NoRoomCode, ex.Code);
    }

    [Fact]
    public void A_certified_document_that_allows_no_annotation_is_refused()
    {
        foreach (var level in new[] { AccessPermissions.NO_CHANGES_PERMITTED, AccessPermissions.FORM_FIELDS_MODIFICATION })
        {
            var ex = Assert.Throws<ApiValidationException>(() => PresignAndSign(Certified(level), keep: null, fieldName: "Seal2"));
            Assert.Equal(PdfSigningService.CertifiedNoChangesCode, ex.Code);
        }
        // Annotations allowed: the stamp goes in as an annotation.
        var (_, placement) = PresignAndSign(Certified(AccessPermissions.ANNOTATION_MODIFICATION), keep: null, fieldName: "Seal2");
        Assert.True(placement!.Annotation);
    }

    // ── no room (review: signatures must not cover each other) ─────────────────────────────────

    [Fact]
    public void Two_signers_side_by_side_without_room_for_the_seal_are_refused()
    {
        // B's reserved box starts right of A's stamp, the page edge is left of it, above and below are taken.
        var occupied = new List<OccupiedRect>
        {
            new() { Page = 1, X = 252, Y = 0, Width = 200, Height = 842 },
            new() { Page = 1, X = 0, Y = 0, Width = 252, Height = 99 },
            new() { Page = 1, X = 0, Y = 161, Width = 252, Height = 681 },
        };
        var ex = Assert.Throws<ApiValidationException>(() => PresignAndSign(Blank(), keep: null, occupied: occupied));
        Assert.Equal(SealPlacement.NoRoomCode, ex.Code);
    }

    [Fact]
    public void A_stamp_over_a_reserved_box_is_refused()
    {
        var occupied = new List<OccupiedRect> { new() { Page = 1, X = 100, Y = 120, Width = 50, Height = 20 } };
        var ex = Assert.Throws<ApiValidationException>(() => PresignAndSign(Blank(), keep: null, occupied: occupied));
        Assert.Equal(SealPlacement.NoRoomCode, ex.Code);
        // A box on another page does not matter.
        occupied[0].Page = 2;
        Assert.NotNull(PresignAndSign(Blank(), keep: null, occupied: occupied).Placement);
    }

    [Fact]
    public void A_stamp_at_the_edge_of_a_small_page_without_room_for_the_seal_is_refused()
    {
        var small = PresignPdfaTests.Pdf(d => d.GetFirstPage().SetMediaBox(new Rectangle(0, 0, 230, 90)).SetCropBox(new Rectangle(0, 0, 230, 90)));
        var ex = Assert.Throws<ApiValidationException>(() => PresignAndSign(small, keep: null, stamp: Stamp(x: 0, y: 0)));
        Assert.Equal(SealPlacement.NoRoomCode, ex.Code);
    }

    [Fact]
    public void The_placement_check_answers_like_presign_without_writing()
    {
        var (certPem, _, _) = TestHelpers.CreateTestCertificate();
        var input = new PreSignInput
        {
            CertificatePem = certPem,
            SignRect = new SignRect { Width = 150, Height = 50 },
            ContentStamp = Stamp(),
            PlaceBesideContentStamp = true,
        };
        var plan = PdfSigningService.PlanContentStamp(input, Blank());
        var (_, placement) = PresignAndSign(Blank(), keep: null);
        Assert.Equal(placement!.Field.X, plan.Placement.Field.X, 1);
        Assert.Equal(placement.Stamp.Y, plan.Placement.Stamp.Y, 1);
        Assert.Equal(ContentStampPlacement.CurrentVersion, plan.Placement.Version);
    }

    // ── CropBox, q/Q, widgets without /P (review) ──────────────────────────────────────────────

    [Fact]
    public void Stamp_and_seal_stay_inside_a_smaller_CropBox_with_an_origin()
    {
        var crop = new Rectangle(100, 200, 380, 400);
        var scan = PresignPdfaTests.Pdf(d => d.GetFirstPage().SetCropBox(crop));
        var (_, placement) = PresignAndSign(scan, keep: null, stamp: Stamp(x: 20, y: 20));
        Assert.True(SealPlacement.Inside(ToRect(placement!.Stamp), crop));
        Assert.True(SealPlacement.Inside(ToRect(placement.Field), crop));
        Assert.False(Overlaps(ToRect(placement.Field), ToRect(placement.Stamp)));
    }

    [Fact]
    public void The_old_page_content_is_wrapped_so_an_open_cm_does_not_move_the_stamp()
    {
        var skewed = PresignPdfaTests.Pdf(d => new PdfCanvas(d.GetFirstPage()).ConcatMatrix(1, 0, 0, 1, 300, 300));
        var (signed, _) = PresignAndSign(skewed, keep: null);
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(signed)));
        var content = System.Text.Encoding.ASCII.GetString(doc.GetPage(1).GetContentBytes());
        // q <old content with the open cm> Q, then the stamp in the untouched coordinate system.
        Assert.StartsWith("q", content.TrimStart());
        int cm = content.IndexOf("300 300 cm", StringComparison.Ordinal);
        int restore = content.IndexOf('Q', cm);
        Assert.True(cm > 0 && restore > cm && restore < content.IndexOf("BT", StringComparison.Ordinal), content);
    }

    [Fact]
    public void A_signature_widget_without_P_counts_as_occupied()
    {
        var withField = PresignPdfaTests.Pdf(d =>
        {
            var form = PdfAcroForm.GetAcroForm(d, true);
            var field = new iText.Forms.Fields.SignatureFormFieldBuilder(d, "Earlier")
                .SetWidgetRectangle(new Rectangle(300, 300, 100, 40)).SetPage(1).CreateSignature();
            form.AddField(field, d.GetFirstPage());
        });
        using var stripped = new MemoryStream();
        using (var edit = new PdfDocument(new PdfReader(new MemoryStream(withField)), new PdfWriter(stripped)))
        {
            edit.GetPage(1).GetAnnotations()[0].GetPdfObject().Remove(PdfName.P);
        }
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(stripped.ToArray())));
        // The widget as written has no /P (iText fills it in memory once the page's annotations are read).
        Assert.Null(doc.GetPage(1).GetPdfObject().GetAsArray(PdfName.Annots).GetAsDictionary(0).Get(PdfName.P));
        var occupied = SealPlacement.OccupiedOn(doc, 1, null);
        Assert.Contains(occupied, r => Math.Abs(r.GetX() - 300) < 0.1 && Math.Abs(r.GetWidth() - 100) < 0.1);
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

        // The second signer's stamp is an annotation: still PDF/A.
        var (second, p2) = PresignAndSign(signed, prepared.KeepPdfA, fieldName: "Seal2", stamp: Stamp(y: 300));
        Assert.True(p2!.Annotation);
        Assert.Empty(PdfAConformanceCheck.Problems(second, PdfAConformance.PDF_A_3B));
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

    private (byte[] Signed, ContentStampPlacement? Placement) PresignAndSign(
        byte[] pdf, PdfAConformance? keep, string fieldName = "Seal1", ContentStampInput? stamp = null, List<OccupiedRect>? occupied = null)
    {
        var (certPem, pfx, password) = TestHelpers.CreateTestCertificate();
        var input = new PreSignInput
        {
            CertificatePem = certPem,
            SignRect = new SignRect { Width = 150, Height = 50 },
            Appearance = new SignatureAppearanceOptions { DescriptionText = "Electronic seal", ShowSignerName = false, ShowReason = false, ShowLocation = false },
            ContentStamp = stamp ?? Stamp(),
            PlaceBesideContentStamp = true,
            Occupied = occupied,
        };
        var (path, hash, placement) = new PdfSigningService().HandlePreSignWithStamp(input, pdf, fieldName, keep);
        _files.Add(path);
        var signed = Convert.FromBase64String(new PdfSigningService().HandleSign(
            new SignInput { Id = "x", SignedHash = SignHash(hash, pfx, password) }, path, certPem, fieldName));
        return (signed, placement);
    }

    private static ContentStampInput Stamp(float x = 50, float y = 100) => new()
    {
        SignRect = new SignRect { X = x, Y = y, Width = 200, Height = 60 },
        SignPageNumber = 1,
        Reason = "Souhlas",
        Location = "Praha",
        SignerName = "Jana Nováková",
        Appearance = new SignatureAppearanceOptions { FontFamily = "Helvetica" },
        DesignWidth = 200,
        DesignHeight = 60,
    };

    private static byte[] Blank() => PresignPdfaTests.Pdf(_ => { });

    /// <summary>A blank page certified (DocMDP) at the given level with an invisible signature.</summary>
    private static byte[] Certified(AccessPermissions level)
    {
        var (_, pfx, password) = TestHelpers.CreateTestCertificate();
        var (chain, key) = PdfCryptoHelper.LoadFromPfx(pfx, password);
        using var output = new MemoryStream();
        var signer = new PdfSigner(new PdfReader(new MemoryStream(Blank())), output, new StampingProperties());
        signer.SetSignerProperties(new SignerProperties().SetFieldName("Cert").SetCertificationLevel(level));
        var signature = new PrivateKeySignature(
            new iText.Bouncycastle.Crypto.PrivateKeyBC((Org.BouncyCastle.Crypto.AsymmetricKeyParameter)key), "SHA-256");
        signer.SignDetached(signature, chain, null, null, null, 0, PdfSigner.CryptoStandard.CMS);
        return output.ToArray();
    }

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
