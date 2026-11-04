using DotNetSigningServer.Models;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;

namespace DotNetSigningServer.Services.SignatureLayout
{
    /// <summary>
    /// Where the seal field goes beside a content stamp (portál #511): right, left, below, above,
    /// always inside the visible page (CropBox). The first spot that covers neither the stamp nor
    /// anything occupied wins; with none free there is no spot (the caller refuses, nothing moves).
    /// </summary>
    public static class SealPlacement
    {
        /// <summary>Gap between the stamp and the seal, in points.</summary>
        public const float Gap = 6f;

        /// <summary>Refusal when the stamp or the seal would cover another signature or leave the page.</summary>
        public const string NoRoomCode = "SEAL_PLACEMENT_NO_ROOM";

        /// <summary>Prefix of <c>/NM</c> on our stamp annotations (portál #511), so later signers find them.</summary>
        public const string StampAnnotationPrefix = "p4pdf-stamp-";

        /// <summary>Key in the signature dictionary with the stamp of that signature: [page x y w h].</summary>
        public static readonly PdfName StampMarkerKey = new("P4PDFContentStamp");

        /// <summary>
        /// The seal spot beside the stamp, or null when none is free. A spot over an unsigned signature
        /// field is taken only when no other spot is free: the field is empty, so it is no obstacle.
        /// </summary>
        public static Rectangle? Beside(Rectangle stamp, float width, float height, Rectangle page, Obstacles obstacles) =>
            Beside(stamp, width, height, page, obstacles.Occupied, obstacles.Unsigned);

        /// <summary>The seal spot beside the stamp, or null when none is free.</summary>
        public static Rectangle? Beside(
            Rectangle stamp, float width, float height, Rectangle page, IReadOnlyList<Rectangle> occupied, IReadOnlyList<Rectangle>? avoidIfPossible = null)
        {
            float w = width > 0 ? width : 160f;
            float h = height > 0 ? height : 56f;
            float top = stamp.GetTop();
            var candidates = new[]
            {
                new Rectangle(stamp.GetRight() + Gap, top - h, w, h),
                new Rectangle(stamp.GetLeft() - Gap - w, top - h, w, h),
                new Rectangle(stamp.GetLeft(), stamp.GetBottom() - Gap - h, w, h),
                new Rectangle(stamp.GetLeft(), top + Gap, w, h),
            };
            // Only spots that fit without moving: a shifted spot could end on the stamp or on someone else.
            var free = candidates.Where(c => Inside(c, page) && !Overlaps(c, stamp) && !occupied.Any(o => Overlaps(c, o))).ToList();
            return free.FirstOrDefault(c => avoidIfPossible?.Any(o => Overlaps(c, o)) != true) ?? free.FirstOrDefault();
        }

        /// <summary>
        /// What is on the page: <c>Occupied</c> must not be covered, <c>Unsigned</c> (empty signature
        /// fields) is only avoided by the seal when another spot is free.
        /// </summary>
        public sealed record Obstacles(IReadOnlyList<Rectangle> Occupied, IReadOnlyList<Rectangle> Unsigned);

        /// <summary>
        /// What a new stamp and seal must not cover on the page: signed signature widgets (with or
        /// without <c>/P</c>), our stamp annotations, stamps recorded in earlier signatures and what the
        /// caller reserved for other signers. Unsigned signature fields are listed apart.
        /// </summary>
        public static Obstacles ObstaclesOn(PdfDocument doc, int pageNumber, IEnumerable<OccupiedRect>? reserved)
        {
            var result = new List<Rectangle>();
            var unsigned = new List<Rectangle>();
            var page = doc.GetPage(pageNumber);
            foreach (var annotation in page.GetAnnotations())
            {
                var dict = annotation.GetPdfObject();
                if (annotation is PdfWidgetAnnotation && IsSignatureWidget(dict))
                {
                    // An empty "sign here" field is no signature: a stamp may go on it.
                    AddIfReal(IsSigned(dict) ? result : unsigned, annotation.GetRectangle());
                }
                else if (annotation is PdfStampAnnotation
                    && dict.GetAsString(PdfName.NM)?.ToUnicodeString().StartsWith(StampAnnotationPrefix, StringComparison.Ordinal) == true)
                {
                    AddIfReal(result, annotation.GetRectangle());
                }
                // A merged field and widget carries its /V here; a stamp drawn into the content is known only from it.
                if (StampMarkerOf(dict) is { } marker && marker.Page == pageNumber) result.Add(marker.Rect);
            }
            foreach (var r in reserved ?? Enumerable.Empty<OccupiedRect>())
            {
                if ((r.Page <= 0 ? 1 : r.Page) == pageNumber && r.Width > 0 && r.Height > 0)
                {
                    result.Add(new Rectangle(r.X, r.Y, r.Width, r.Height));
                }
            }
            return new Obstacles(result, unsigned);
        }

        /// <summary>Hard obstacles only, see <see cref="ObstaclesOn"/>.</summary>
        public static IReadOnlyList<Rectangle> OccupiedOn(PdfDocument doc, int pageNumber, IEnumerable<OccupiedRect>? reserved) =>
            ObstaclesOn(doc, pageNumber, reserved).Occupied;

        /// <summary>The stamp recorded in a signature dictionary as [page x y w h].</summary>
        public static PdfArray StampMarker(int page, Rectangle rect) =>
            new(new float[] { page, rect.GetX(), rect.GetY(), rect.GetWidth(), rect.GetHeight() });

        internal static bool Inside(Rectangle r, Rectangle page) =>
            r.GetLeft() >= page.GetLeft() - 0.01f && r.GetBottom() >= page.GetBottom() - 0.01f
            && r.GetRight() <= page.GetRight() + 0.01f && r.GetTop() <= page.GetTop() + 0.01f;

        internal static bool Overlaps(Rectangle a, Rectangle b) =>
            a.GetLeft() < b.GetRight() && b.GetLeft() < a.GetRight()
            && a.GetBottom() < b.GetTop() && b.GetBottom() < a.GetTop();

        private static bool IsSignatureWidget(PdfDictionary widget)
        {
            // /FT is inherited: on the widget itself, or on its field up the /Parent chain.
            for (var d = widget; d != null; d = d.GetAsDictionary(PdfName.Parent))
            {
                var ft = d.GetAsName(PdfName.FT);
                if (ft != null) return PdfName.Sig.Equals(ft);
            }
            return false;
        }

        private static bool IsSigned(PdfDictionary widget)
        {
            // /V is inherited like /FT.
            for (var d = widget; d != null; d = d.GetAsDictionary(PdfName.Parent))
            {
                if (d.Get(PdfName.V) is { } v && !(v is PdfNull)) return true;
            }
            return false;
        }

        private static (int Page, Rectangle Rect)? StampMarkerOf(PdfDictionary widget)
        {
            for (var d = widget; d != null; d = d.GetAsDictionary(PdfName.Parent))
            {
                var marker = d.GetAsDictionary(PdfName.V)?.GetAsArray(StampMarkerKey);
                if (marker == null) continue;
                if (marker.Size() != 5) return null;
                var v = Enumerable.Range(0, 5).Select(i => marker.GetAsNumber(i)?.FloatValue()).ToArray();
                if (v.Any(n => n == null) || v[3] <= 0 || v[4] <= 0) return null;
                return ((int)v[0]!.Value, new Rectangle(v[1]!.Value, v[2]!.Value, v[3]!.Value, v[4]!.Value));
            }
            return null;
        }

        private static void AddIfReal(List<Rectangle> result, PdfArray? rect)
        {
            var r = rect?.ToRectangle();
            if (r != null && r.GetWidth() > 0 && r.GetHeight() > 0) result.Add(r);
        }
    }
}
