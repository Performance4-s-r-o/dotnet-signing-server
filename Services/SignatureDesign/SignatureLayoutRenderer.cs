using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
    /// <summary>
    /// Decoded images of a signature appearance; a missing one leaves its zone empty. Each image becomes one
    /// image XObject per document, however often it is drawn (repeat tiles, page stamp plus field appearance);
    /// the XObjects live with the document, not with this object, so an instance may be reused safely.
    /// </summary>
    public sealed class SignatureLayoutImages
    {
        public ImageData? Sig { get; init; }
        public ImageData? Logo { get; init; }
        public ImageData? Image { get; init; }
        public ImageData? Background { get; init; }

        // Weak on the document: a finished document and its buffers are never kept alive by the cache.
        private static readonly ConditionalWeakTable<PdfDocument, Dictionary<ImageData, PdfImageXObject>> XObjects = new();

        public ImageData? For(string zoneKind) => zoneKind switch
        {
            "sig" => Sig,
            "logo" => Logo,
            "image" => Image,
            _ => null,
        };

        internal static PdfImageXObject XObject(ImageData image, PdfDocument doc)
        {
            var perDoc = XObjects.GetValue(doc, _ => new Dictionary<ImageData, PdfImageXObject>());
            lock (perDoc)
            {
                if (!perDoc.TryGetValue(image, out var xobject))
                {
                    xobject = new PdfImageXObject(image);
                    perDoc[image] = xobject;
                }
                return xobject;
            }
        }

        /// <summary>Images the layout will actually draw (zones, background image, watermark logo).</summary>
        internal IEnumerable<ImageData> Used(SignatureLayoutResult layout)
        {
            foreach (var zone in layout.Zones)
                if (For(zone.Kind) is { } img) yield return img;
            if (layout.Background.Kind == "image" && Background != null) yield return Background;
            if (layout.Background.Kind == "watermark" && Logo != null) yield return Logo;
        }
    }

    /// <summary>The appearance needs something the target document does not allow (e.g. transparency in PDF/A-1).</summary>
    public sealed class SignatureAppearanceUnsupportedException : Exception
    {
        public SignatureAppearanceUnsupportedException(string code, string message) : base(message) => Code = code;

        public string Code { get; }
    }

    /// <summary>
    /// Draws a <see cref="SignatureLayoutResult"/> into one <see cref="PdfFormXObject"/>: the same object goes
    /// into the page content or an annotation (image signature) and into the signature field (certificate),
    /// so both look the same. It draws only what the engine computed, with the same primitives as the
    /// portal's SVG preview (<c>lib/signature-layout/svg.ts</c>), so the preview matches the PDF.
    /// Muted text and patterns are mixed solid colours (allowed in PDF/A-1); only a background image and
    /// the watermark need transparency.
    /// </summary>
    public static class SignatureLayoutRenderer
    {
        /// <summary>Muted lines (reason, location, date) and separators: text colour mixed with 30 % of <see cref="MutedBase"/>.</summary>
        public const double MutedMix = 0.7;

        public const string TransparencyNotAllowed = "APPEARANCE_TRANSPARENCY_NOT_ALLOWED";

        private static readonly Regex Hex = new("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant);

        /// <param name="transparencyAllowed">False for PDF/A-1: a translucent background image or watermark is then refused.</param>
        public static PdfFormXObject Render(PdfDocument doc, SignatureLayoutResult layout, SignatureLayoutImages images, bool transparencyAllowed = true)
        {
            if (!transparencyAllowed)
            {
                // Fully opaque (opacity 100, alpha 1) needs no transparency group and PDF/A-1 accepts it.
                if (layout.Background.Kind is "image" or "watermark" && (layout.Background.Alpha ?? 1) < 1)
                    throw new SignatureAppearanceUnsupportedException(TransparencyNotAllowed,
                        "A background image or watermark needs transparency, which PDF/A-1 does not allow.");
                // An alpha channel or partial tRNS becomes an image with a soft mask, which PDF/A-1 refuses on close.
                if (images.Used(layout).Any(img => img.GetImageMask() != null))
                    throw new SignatureAppearanceUnsupportedException(TransparencyNotAllowed,
                        "An image of the appearance has transparency, which PDF/A-1 does not allow.");
            }

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

            DrawBackground(canvas, doc, layout, images, W, H);

            foreach (var zone in layout.Zones)
            {
                var image = images.For(zone.Kind);
                if (image != null) DrawContained(canvas, SignatureLayoutImages.XObject(image, doc), image, zone.Rect, H);
            }

            if (layout.Text != null) DrawText(canvas, layout, H);

            canvas.RestoreState();
            canvas.Release();
            return xobject;
        }

        private static void DrawBackground(PdfCanvas canvas, PdfDocument doc, SignatureLayoutResult layout, SignatureLayoutImages images, float W, float H)
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
                    {
                        var img = images.Background;
                        var x = SignatureLayoutImages.XObject(img, doc);
                        canvas.SaveState();
                        if ((bg.Alpha ?? 1) < 1) canvas.SetExtGState(new PdfExtGState().SetFillOpacity((float)bg.Alpha!.Value));
                        if (bg.Fit == "repeat")
                        {
                            // Tiles of H x H, the image contained in each, from the top left corner.
                            for (double ty = 0; ty < H; ty += H)
                                for (double tx = 0; tx < W; tx += H)
                                    DrawContained(canvas, x, img, new LayoutRect { X = tx, Y = ty, W = H, H = H }, H);
                        }
                        else
                        {
                            double s = Math.Max(W / img.GetWidth(), H / img.GetHeight());
                            double dw = img.GetWidth() * s;
                            double dh = img.GetHeight() * s;
                            canvas.AddXObjectFittedIntoRectangle(x, new Rectangle((float)((W - dw) / 2), (float)((H - dh) / 2), (float)dw, (float)dh));
                        }
                        canvas.RestoreState();
                        break;
                    }
                case "watermark" when images.Logo != null && bg.Rect != null:
                    canvas.SaveState();
                    if ((bg.Alpha ?? 1) < 1) canvas.SetExtGState(new PdfExtGState().SetFillOpacity((float)bg.Alpha!.Value));
                    DrawContained(canvas, SignatureLayoutImages.XObject(images.Logo, doc), images.Logo, bg.Rect, H);
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
            // On an opaque base the pattern's alpha is a mixed solid colour (as in the SVG preview).
            var color = Color(Mix(bg.PatternColor, bg.Color, bg.Alpha ?? 1));
            canvas.SaveState();
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
        private static void DrawContained(PdfCanvas canvas, PdfImageXObject xobject, ImageData image, LayoutRect r, float frameHeight)
        {
            double iw = image.GetWidth();
            double ih = image.GetHeight();
            if (iw <= 0 || ih <= 0 || r.W <= 0 || r.H <= 0) return;
            double s = Math.Min(r.W / iw, r.H / ih);
            double dw = iw * s;
            double dh = ih * s;
            double x = r.X + (r.W - dw) / 2;
            double top = r.Y + (r.H - dh) / 2;
            canvas.AddXObjectFittedIntoRectangle(xobject, new Rectangle((float)x, (float)(frameHeight - top - dh), (float)dw, (float)dh));
        }

        private static void DrawText(PdfCanvas canvas, SignatureLayoutResult layout, float H)
        {
            var text = layout.Text!;
            var family = AppFonts.FamilyFromLegacyName(layout.FontFamily);
            PdfFont? regular = null;
            PdfFont? bold = null;
            var primary = Color(layout.TextColor);
            var muted = Color(Mix(layout.TextColor, MutedBase(layout.Background), MutedMix));
            float size = (float)text.FontSize;
            foreach (var line in text.Lines)
            {
                foreach (var run in line.Runs)
                {
                    if (string.IsNullOrEmpty(run.Text)) continue;
                    var font = run.Bold ? bold ??= AppFonts.Load(family, bold: true) : regular ??= AppFonts.Load(family);
                    canvas.BeginText()
                        .SetFontAndSize(font, size)
                        .SetFillColor(run.Tone == "muted" ? muted : primary)
                        .MoveText(run.X, H - line.Baseline)
                        .ShowText(run.Text)
                        .EndText();
                }
            }
        }

        /// <summary>What muted text is mixed with: the solid background colour, otherwise white.</summary>
        internal static string MutedBase(LayoutBackground bg) =>
            bg.Kind is "color" or "pattern" && bg.Color != null ? bg.Color : "#ffffff";

        /// <summary>Solid colour <paramref name="a"/> mixed with <paramref name="b"/> at <paramref name="t"/>; same as the portal's <c>mixColor</c>.</summary>
        internal static string Mix(string? a, string? b, double t)
        {
            int pa = Convert.ToInt32(Valid(a).Substring(1), 16);
            int pb = Convert.ToInt32(Valid(b).Substring(1), 16);
            int Channel(int shift)
            {
                int ca = (pa >> shift) & 255;
                int cb = (pb >> shift) & 255;
                return (int)Math.Floor(ca * t + cb * (1 - t) + 0.5);
            }
            return $"#{Channel(16):x2}{Channel(8):x2}{Channel(0):x2}";
        }

        private static string Valid(string? hex) => hex != null && Hex.IsMatch(hex) ? hex : "#000000";

        private static DeviceRgb Color(string? hex) => PdfCryptoHelper.ParseHexColor(Valid(hex));
    }
}
