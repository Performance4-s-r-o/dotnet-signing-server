using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;
using iText.Kernel.Pdf.Xobject;

namespace DotNetSigningServer.Services.SignatureDesign
{
    /// <summary>Decoded images of a signature appearance; a missing one leaves its zone empty.</summary>
    public sealed class SignatureLayoutImages
    {
        public ImageData? Sig { get; init; }
        public ImageData? Logo { get; init; }
        public ImageData? Image { get; init; }
        public ImageData? Background { get; init; }

        public ImageData? For(string zoneKind) => zoneKind switch
        {
            "sig" => Sig,
            "logo" => Logo,
            "image" => Image,
            _ => null,
        };
    }

    /// <summary>
    /// Draws a <see cref="SignatureLayoutResult"/> into one <see cref="PdfFormXObject"/>: the same object goes
    /// into the page content or an annotation (image signature) and into the signature field (certificate),
    /// so both look the same. It draws only what the engine computed, with the same primitives as the
    /// portal's SVG preview (<c>lib/signature-layout/svg.ts</c>), so the preview matches the PDF.
    /// Transparency needs PDF/A-2 or later, never PDF/A-1.
    /// </summary>
    public static class SignatureLayoutRenderer
    {
        /// <summary>Opacity of the muted lines (reason, location, date) and separators.</summary>
        public const float MutedAlpha = 0.7f;

        public static PdfFormXObject Render(PdfDocument doc, SignatureLayoutResult layout, SignatureLayoutImages images)
        {
            float W = (float)layout.Width;
            float H = (float)layout.Height;
            var xobject = new PdfFormXObject(new Rectangle(0, 0, W, H));
            var canvas = new PdfCanvas(xobject, doc);
            canvas.SaveState();

            // Everything is clipped to the (rounded) frame; the corners stay transparent.
            float radius = (float)layout.Radius;
            if (radius > 0) canvas.RoundRectangle(0, 0, W, H, radius);
            else canvas.Rectangle(0, 0, W, H);
            canvas.Clip().EndPath();

            DrawBackground(canvas, layout, images, W, H);

            foreach (var zone in layout.Zones)
            {
                var image = images.For(zone.Kind);
                if (image != null) DrawContained(canvas, image, zone.Rect, H);
            }

            if (layout.Text != null) DrawText(canvas, doc, layout, H);

            canvas.RestoreState();
            canvas.Release();
            return xobject;
        }

        private static void DrawBackground(PdfCanvas canvas, SignatureLayoutResult layout, SignatureLayoutImages images, float W, float H)
        {
            var bg = layout.Background;
            switch (bg.Kind)
            {
                case "color":
                    canvas.SetFillColor(Color(bg.Color)).Rectangle(0, 0, W, H).Fill();
                    break;
                case "pattern":
                    canvas.SetFillColor(Color(bg.Color)).Rectangle(0, 0, W, H).Fill();
                    DrawPattern(canvas, bg, W, H);
                    break;
                case "image" when images.Background != null:
                    canvas.SaveState();
                    canvas.SetExtGState(new PdfExtGState().SetFillOpacity((float)(bg.Alpha ?? 1)));
                    if (bg.Fit == "repeat")
                    {
                        // Tiles of H x H, the image contained in each, from the top left corner.
                        for (double ty = 0; ty < H; ty += H)
                            for (double tx = 0; tx < W; tx += H)
                                DrawContained(canvas, images.Background, new LayoutRect { X = tx, Y = ty, W = H, H = H }, H);
                    }
                    else
                    {
                        var img = images.Background;
                        double s = Math.Max(W / img.GetWidth(), H / img.GetHeight());
                        double dw = img.GetWidth() * s;
                        double dh = img.GetHeight() * s;
                        canvas.AddImageFittedIntoRectangle(img, new Rectangle((float)((W - dw) / 2), (float)((H - dh) / 2), (float)dw, (float)dh), false);
                    }
                    canvas.RestoreState();
                    break;
                case "watermark" when images.Logo != null && bg.Rect != null:
                    canvas.SaveState();
                    canvas.SetExtGState(new PdfExtGState().SetFillOpacity((float)(bg.Alpha ?? 1)));
                    DrawContained(canvas, images.Logo, bg.Rect, H);
                    canvas.RestoreState();
                    break;
            }
        }

        /// <summary>Lines, circles and dots of a pattern; the same loops as <c>patternPrimitives</c> in svg.ts.</summary>
        internal static (List<(double X1, double Y1, double X2, double Y2)> Lines, List<(double Cx, double Cy, double R)> Circles, List<(double X, double Y)> Dots)
            PatternPrimitives(string pattern, double spacing, double width, double height)
        {
            var lines = new List<(double, double, double, double)>();
            var circles = new List<(double, double, double)>();
            var dots = new List<(double, double)>();
            double s = spacing;
            if (s <= 0) return (lines, circles, dots);
            switch (pattern)
            {
                case "diag":
                    {
                        double step = s * Math.Sqrt(2);
                        for (double c = 0; c <= width + height; c += step) lines.Add((c - height, height, c, 0));
                        break;
                    }
                case "grid":
                    for (double x = 0; x <= width; x += s) lines.Add((x, 0, x, height));
                    for (double y = 0; y <= height; y += s) lines.Add((0, y, width, y));
                    break;
                case "dots":
                    for (double y = s / 2; y < height; y += s)
                        for (double x = s / 2; x < width; x += s) dots.Add((x, y));
                    break;
                default:
                    {
                        double max = Math.Sqrt(width * width + height * height);
                        for (double r = s; r <= max; r += s)
                        {
                            circles.Add((0, height, r));
                            circles.Add((width, 0, r));
                        }
                        break;
                    }
            }
            return (lines, circles, dots);
        }

        private static void DrawPattern(PdfCanvas canvas, LayoutBackground bg, float W, float H)
        {
            var (lines, circles, dots) = PatternPrimitives(bg.Pattern ?? "diag", bg.Spacing ?? 0, W, H);
            var color = Color(bg.PatternColor);
            float alpha = (float)(bg.Alpha ?? 1);
            canvas.SaveState();
            canvas.SetExtGState(new PdfExtGState().SetStrokeOpacity(alpha).SetFillOpacity(alpha));
            canvas.SetStrokeColor(color).SetFillColor(color).SetLineWidth((float)(bg.LineWidth ?? 1));
            // y is flipped: the engine measures from the top, PDF from the bottom.
            foreach (var (x1, y1, x2, y2) in lines)
                canvas.MoveTo(x1, H - y1).LineTo(x2, H - y2);
            if (lines.Count > 0) canvas.Stroke();
            foreach (var (cx, cy, r) in circles)
                canvas.Circle(cx, H - cy, r);
            if (circles.Count > 0) canvas.Stroke();
            double dot = bg.DotRadius ?? 0;
            if (dot > 0)
            {
                foreach (var (x, y) in dots) canvas.Circle(x, H - y, dot);
                if (dots.Count > 0) canvas.Fill();
            }
            canvas.RestoreState();
        }

        /// <summary>The image fitted into the rect keeping its aspect ratio, centred (SVG <c>xMidYMid meet</c>).</summary>
        private static void DrawContained(PdfCanvas canvas, ImageData image, LayoutRect r, float frameHeight)
        {
            double iw = image.GetWidth();
            double ih = image.GetHeight();
            if (iw <= 0 || ih <= 0 || r.W <= 0 || r.H <= 0) return;
            double s = Math.Min(r.W / iw, r.H / ih);
            double dw = iw * s;
            double dh = ih * s;
            double x = r.X + (r.W - dw) / 2;
            double top = r.Y + (r.H - dh) / 2;
            canvas.AddImageFittedIntoRectangle(image, new Rectangle((float)x, (float)(frameHeight - top - dh), (float)dw, (float)dh), false);
        }

        private static void DrawText(PdfCanvas canvas, PdfDocument doc, SignatureLayoutResult layout, float H)
        {
            var text = layout.Text!;
            var family = AppFonts.FamilyFromLegacyName(layout.FontFamily);
            PdfFont? regular = null;
            PdfFont? bold = null;
            var color = Color(layout.TextColor);
            float size = (float)text.FontSize;
            foreach (var line in text.Lines)
            {
                foreach (var run in line.Runs)
                {
                    if (string.IsNullOrEmpty(run.Text)) continue;
                    var font = run.Bold ? bold ??= AppFonts.Load(family, bold: true) : regular ??= AppFonts.Load(family);
                    canvas.SaveState();
                    if (run.Tone == "muted") canvas.SetExtGState(new PdfExtGState().SetFillOpacity(MutedAlpha));
                    canvas.BeginText()
                        .SetFontAndSize(font, size)
                        .SetFillColor(color)
                        .MoveText(run.X, H - line.Baseline)
                        .ShowText(run.Text)
                        .EndText();
                    canvas.RestoreState();
                }
            }
        }

        private static DeviceRgb Color(string? hex) =>
            string.IsNullOrWhiteSpace(hex) ? new DeviceRgb(0, 0, 0) : PdfCryptoHelper.ParseHexColor(hex);
    }
}
