using DotNetSigningServer.Controllers;
using ZXing;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// Reading codes off a page: which symbologies are looked for, and where the
/// answer says they are.
///
/// Both halves were broken in ways that looked like they worked. The format list
/// held only 2D symbologies, so a Code128 — which this server can WRITE — came
/// back as "no codes found" rather than as an error. And the position was the
/// first decoder point in 300 DPI raster pixels, straight into a field callers
/// read as PDF units: about four times too far, measured from the wrong edge,
/// and taken from whichever enlarged variant happened to decode.
/// </summary>
public class FindCodesTests
{
    // A4 at 300 DPI is 2480 × 3508 px; the page is 842 pt tall.
    private const double A4HeightPx = 3508;
    private const double A4HeightPt = 841.92;

    private static ResultPoint[] Rect(float left, float top, float right, float bottom) =>
        new[]
        {
            new ResultPoint(left, top),
            new ResultPoint(right, top),
            new ResultPoint(right, bottom),
            new ResultPoint(left, bottom),
        };

    [Fact]
    public void ParseFormats_Default_FindsLinearCodesToo()
    {
        var formats = PdfUtilityApiController.ParseFormats("any");

        // The actual reported gap: Code128 was unreadable.
        Assert.Contains(BarcodeFormat.CODE_128, formats);
        Assert.Contains(BarcodeFormat.QR_CODE, formats);
        Assert.Contains(BarcodeFormat.EAN_13, formats);
    }

    [Fact]
    public void ParseFormats_Default_LeavesOutTheOnesThatDecodeNoise()
    {
        var formats = PdfUtilityApiController.ParseFormats("any");

        // No mandatory check digit: on a 300 DPI render of a table these report
        // values that are not on the page. A confident wrong answer is worse
        // than a missing one, so they are opt-in.
        Assert.DoesNotContain(BarcodeFormat.CODE_39, formats);
        Assert.DoesNotContain(BarcodeFormat.ITF, formats);
        Assert.DoesNotContain(BarcodeFormat.CODABAR, formats);
    }

    [Fact]
    public void ParseFormats_AskingForLinearCodes_IncludesTheUncheckedOnes()
    {
        var formats = PdfUtilityApiController.ParseFormats("1d");

        Assert.Contains(BarcodeFormat.CODE_39, formats);
        Assert.Contains(BarcodeFormat.ITF, formats);
        Assert.Contains(BarcodeFormat.CODE_128, formats);
        // Asking for linear codes must not quietly scan for QR as well.
        Assert.DoesNotContain(BarcodeFormat.QR_CODE, formats);
    }

    [Theory]
    [InlineData("code128", BarcodeFormat.CODE_128)]
    [InlineData("code-128", BarcodeFormat.CODE_128)]
    [InlineData("ean13", BarcodeFormat.EAN_13)]
    [InlineData("qr", BarcodeFormat.QR_CODE)]
    public void ParseFormats_NamedFormat_IsTheOnlyOneScannedFor(string codeType, BarcodeFormat expected)
    {
        var formats = PdfUtilityApiController.ParseFormats(codeType);

        Assert.Single(formats);
        Assert.Equal(expected, formats[0]);
    }

    [Fact]
    public void ToPdfBoundingBox_ConvertsPixelsToPointsAndFlipsTheAxis()
    {
        // 300 px from the top of the page, 600 px wide, 150 px tall.
        var box = PdfUtilityApiController.ToPdfBoundingBox(
            Rect(left: 600, top: 300, right: 1200, bottom: 450),
            scale: 1.0,
            pageHeightPx: A4HeightPx);

        Assert.NotNull(box);
        // 600 px ÷ 300 DPI × 72 = 144 pt from the left.
        Assert.Equal(144.0, box!.X, 1);
        Assert.Equal(144.0, box.Width, 1);
        Assert.Equal(36.0, box.Height, 1);
        // The bottom edge is 450 px down the page, so 842 − 108 pt up from the
        // bottom. Reporting the raster number would have put it near the top.
        Assert.Equal(A4HeightPt - 108.0, box.Y, 1);
    }

    [Fact]
    public void ToPdfBoundingBox_UndoesTheVariantEnlargement()
    {
        var atPageScale = PdfUtilityApiController.ToPdfBoundingBox(
            Rect(600, 300, 1200, 450), scale: 1.0, pageHeightPx: A4HeightPx);

        // The same code, found in the 2× copy: every coordinate is doubled, and
        // so is the height the points are measured against.
        var atDoubleScale = PdfUtilityApiController.ToPdfBoundingBox(
            Rect(1200, 600, 2400, 900), scale: 2.0, pageHeightPx: A4HeightPx);

        Assert.Equal(atPageScale!.X, atDoubleScale!.X, 1);
        Assert.Equal(atPageScale.Y, atDoubleScale.Y, 1);
        Assert.Equal(atPageScale.Width, atDoubleScale.Width, 1);
        Assert.Equal(atPageScale.Height, atDoubleScale.Height, 1);
    }

    [Fact]
    public void ToPdfBoundingBox_UsesEveryCornerNotJustTheFirst()
    {
        // A linear code gives two points on one baseline. Taking the first one
        // alone reported a dot; the box has to span both.
        var box = PdfUtilityApiController.ToPdfBoundingBox(
            new[] { new ResultPoint(600, 300), new ResultPoint(1200, 300) },
            scale: 1.0,
            pageHeightPx: A4HeightPx);

        Assert.NotNull(box);
        Assert.Equal(144.0, box!.Width, 1);
        Assert.Equal(0.0, box.Height, 1);
    }

    [Fact]
    public void ToPdfBoundingBox_NoPoints_IsNullNotAZeroCorner()
    {
        // Zero would read as "bottom-left corner of the page", which is a claim
        // about where the code is. Absence has to stay absence.
        Assert.Null(PdfUtilityApiController.ToPdfBoundingBox(null, 1.0, A4HeightPx));
        Assert.Null(PdfUtilityApiController.ToPdfBoundingBox(Array.Empty<ResultPoint>(), 1.0, A4HeightPx));
    }
}
