using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.SignatureLayout;
using DotNetSigningServer.Tests.Helpers;
using iText.Forms;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Signatures;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// Portál #511: a client places on the CropBox as displayed (pdf.js viewport); dotnet converts that
/// frame to user space once, for the stamp, the seal, the field and the placement check alike.
/// </summary>
public class PageFrameTests : IDisposable
{
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch { }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Client_frame_and_user_space_convert_both_ways(int rotation)
    {
        var frame = FrameOf(PresignPdfaTests.Pdf(d => d.GetFirstPage().SetCropBox(new Rectangle(30, 40, 500, 700)).SetRotation(rotation)));
        var view = new Rectangle(12, 34, 100, 20);
        var page = frame.ToPage(view);
        var back = frame.ToView(page);
        Assert.Equal(view.GetX(), back.GetX(), 2);
        Assert.Equal(view.GetY(), back.GetY(), 2);
        Assert.Equal(view.GetWidth(), back.GetWidth(), 2);
        Assert.Equal(view.GetHeight(), back.GetHeight(), 2);
        Assert.True(SealPlacement.Inside(page, frame.Crop));
        // The matrix maps the same corner as the rect.
        var m = frame.ToPageMatrix();
        float Px(float x, float y) => m[0] * x + m[2] * y + m[4];
        float Py(float x, float y) => m[1] * x + m[3] * y + m[5];
        var xs = new[] { Px(12, 34), Px(112, 54) };
        var ys = new[] { Py(12, 34), Py(112, 54) };
        Assert.Equal(page.GetX(), xs.Min(), 2);
        Assert.Equal(page.GetY(), ys.Min(), 2);
    }

    [Fact]
    public void Rotate_90_displays_the_page_turned_with_the_client_frame_from_the_CropBox_corner()
    {
        var frame = FrameOf(PresignPdfaTests.Pdf(d => d.GetFirstPage().SetRotation(90)));
        Assert.Equal(842, frame.Width, 1);
        Assert.Equal(595, frame.Height, 1);
        // pdf.js: displayed x = user y, displayed y from the top = user x.
        var page = frame.ToPage(new Rectangle(50, 100, 200, 60));
        Assert.Equal(new Rectangle(595 - 100 - 60, 50, 60, 200).ToString(), page.ToString());
    }

    [Fact]
    public void A_certificate_field_on_a_plain_page_lands_exactly_where_it_was_placed()
    {
        // No CropBox origin, no /Rotate: as before (Fortify position must not move).
        var widget = FieldOf(PresignPdfaTests.Pdf(_ => { }), new SignRect { X = 50, Y = 100, Width = 200, Height = 60 });
        Assert.Equal("[50 100 250 160 ]", widget.ToString());
    }

    [Fact]
    public void A_certificate_field_is_moved_by_the_CropBox_origin()
    {
        var pdf = PresignPdfaTests.Pdf(d => d.GetFirstPage().SetCropBox(new Rectangle(100, 200, 380, 400)));
        var widget = FieldOf(pdf, new SignRect { X = 20, Y = 30, Width = 200, Height = 60 }).ToRectangle();
        Assert.Equal(120, widget.GetX(), 1);
        Assert.Equal(230, widget.GetY(), 1);
    }

    [Fact]
    public void A_certificate_field_on_a_turned_page_is_turned_with_it()
    {
        var pdf = PresignPdfaTests.Pdf(d => d.GetFirstPage().SetRotation(90));
        var widget = FieldOf(pdf, new SignRect { X = 50, Y = 100, Width = 200, Height = 60 }).ToRectangle();
        Assert.Equal(new Rectangle(435, 50, 60, 200).ToString(), widget.ToString());
    }

    [Fact]
    public void Stamp_and_seal_on_a_turned_page_with_a_CropBox_origin_sit_where_placed()
    {
        var crop = new Rectangle(20, 30, 560, 800);
        var pdf = PresignPdfaTests.Pdf(d => d.GetFirstPage().SetCropBox(crop).SetRotation(90));
        var frame = FrameOf(pdf);
        var (signed, placement) = PresignAndSign(pdf, ContentStampPresignTests.Stamp(x: 50, y: 100));

        // The placement answers in the client frame, the seal right of the stamp as displayed.
        Assert.Equal(50, placement!.Stamp.X, 1);
        Assert.Equal(100, placement.Stamp.Y, 1);
        Assert.Equal(50 + 200 + SealPlacement.Gap, placement.Field.X, 1);
        Assert.True(SealPlacement.Inside(Rect(placement.Field), frame.Box));

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(signed)));
        var util = new SignatureUtil(doc);
        Assert.True(util.ReadSignatureData("Seal1").VerifySignatureIntegrityAndAuthenticity());
        var widget = PdfAcroForm.GetAcroForm(doc, false)!.GetField("Seal1").GetWidgets()[0].GetRectangle().ToRectangle();
        var expected = frame.ToPage(Rect(placement.Field));
        Assert.Equal(expected.ToString(), widget.ToString());
        Assert.True(SealPlacement.Inside(widget, crop));
        // The stamp is drawn turned into the client frame.
        var m = frame.ToPageMatrix();
        var content = System.Text.Encoding.ASCII.GetString(doc.GetPage(1).GetContentBytes());
        Assert.Contains($"0 1 -1 0 {m[4]:0.##} {m[5]:0.##} cm", content);
    }

    [Fact]
    public void On_a_turned_page_the_second_signer_sees_the_first_stamp_and_its_annotation_is_turned()
    {
        var pdf = PresignPdfaTests.Pdf(d => d.GetFirstPage().SetRotation(90));
        var (first, p1) = PresignAndSign(pdf, ContentStampPresignTests.Stamp(x: 50, y: 100));
        // The same spot again: the first stamp (marker in user space) is found in the client frame.
        var ex = Assert.Throws<ApiValidationException>(() => PresignAndSign(first, ContentStampPresignTests.Stamp(x: 50, y: 100), "Seal2"));
        Assert.Equal(SealPlacement.NoRoomCode, ex.Code);

        var (second, p2) = PresignAndSign(first, ContentStampPresignTests.Stamp(x: 50, y: 300), "Seal2");
        Assert.True(p2!.Annotation);
        Assert.False(Overlaps(Rect(p2.Field), Rect(p1!.Field)));
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(second)));
        var stamp = doc.GetPage(1).GetAnnotations().OfType<iText.Kernel.Pdf.Annot.PdfStampAnnotation>().Single();
        var appearance = (PdfStream)stamp.GetNormalAppearanceObject();
        Assert.Equal("[0 1 -1 0 0 0 ]", appearance.GetAsArray(PdfName.Matrix).ToString());
        // Annotation rect in user space: the client box turned.
        Assert.Equal(60, stamp.GetRectangle().ToRectangle().GetWidth(), 1);
    }

    [Fact]
    public void A_reserved_box_is_read_in_the_client_frame()
    {
        var pdf = PresignPdfaTests.Pdf(d => d.GetFirstPage().SetCropBox(new Rectangle(100, 200, 380, 400)));
        // Right of the stamp in client coordinates: the seal goes left, not over it.
        var occupied = new List<OccupiedRect> { new() { Page = 1, X = 256, Y = 0, Width = 124, Height = 400 } };
        var (_, placement) = PresignAndSign(pdf, ContentStampPresignTests.Stamp(x: 50, y: 100), occupied: occupied);
        Assert.False(Overlaps(Rect(placement!.Field), new Rectangle(256, 0, 124, 400)));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static PageFrame FrameOf(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return PageFrame.Of(doc.GetPage(1));
    }

    private PdfArray FieldOf(byte[] pdf, SignRect rect)
    {
        var (certPem, _, _) = TestHelpers.CreateTestCertificate();
        var (path, _) = new PdfSigningService().HandlePreSign(new PreSignInput { CertificatePem = certPem, SignRect = rect }, pdf, "F1");
        _files.Add(path);
        using var doc = new PdfDocument(new PdfReader(path));
        return PdfAcroForm.GetAcroForm(doc, false)!.GetField("F1").GetWidgets()[0].GetRectangle();
    }

    private (byte[] Signed, ContentStampPlacement? Placement) PresignAndSign(
        byte[] pdf, ContentStampInput stamp, string fieldName = "Seal1", List<OccupiedRect>? occupied = null)
    {
        var (certPem, pfx, password) = TestHelpers.CreateTestCertificate();
        var input = new PreSignInput
        {
            CertificatePem = certPem,
            SignRect = new SignRect { Width = 150, Height = 50 },
            ContentStamp = stamp,
            PlaceBesideContentStamp = true,
            Occupied = occupied,
        };
        var (path, hash, placement) = new PdfSigningService().HandlePreSignWithStamp(input, pdf, fieldName);
        _files.Add(path);
        var signed = Convert.FromBase64String(new PdfSigningService().HandleSign(
            new SignInput { Id = "x", SignedHash = ContentStampPresignTests.SignHash(hash, pfx, password) }, path, certPem, fieldName));
        return (signed, placement);
    }

    private static Rectangle Rect(SignRect r) => new(r.X, r.Y, r.Width, r.Height);

    private static bool Overlaps(Rectangle a, Rectangle b) =>
        a.GetLeft() < b.GetRight() && b.GetLeft() < a.GetRight() && a.GetBottom() < b.GetTop() && b.GetBottom() < a.GetTop();
}
