using DotNetSigningServer.Controllers;
using ZXing;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// Where a scan says a code is.
///
/// The position used to be the first decoder point in 300 DPI raster pixels,
/// straight into a field callers read as PDF units: about four times too far,
/// measured from the wrong edge, and taken from whichever enlarged variant
/// happened to decode.
///
/// Which symbologies get looked for is checked in CodeFormatsTests, next to the
/// catalogue that decides it.
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
