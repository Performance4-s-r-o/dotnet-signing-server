using iText.Forms;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;

namespace DotNetSigningServer.Services.SignatureLayout
{
    /// <summary>
    /// Where the seal field goes beside a content stamp (portál #511): right, left, below, above,
    /// always inside the page. The first spot that covers neither the stamp nor another signature
    /// field on the page wins; with none free the first spot inside the page that misses the stamp.
    /// </summary>
    public static class SealPlacement
    {
        /// <summary>Gap between the stamp and the seal, in points.</summary>
        public const float Gap = 6f;

        public static Rectangle Beside(Rectangle stamp, float width, float height, Rectangle page, IReadOnlyList<Rectangle> occupied)
        {
            float w = Math.Min(width > 0 ? width : 160f, page.GetWidth());
            float h = Math.Min(height > 0 ? height : 56f, page.GetHeight());
            float top = stamp.GetTop();
            var candidates = new[]
            {
                new Rectangle(stamp.GetRight() + Gap, top - h, w, h),
                new Rectangle(stamp.GetLeft() - Gap - w, top - h, w, h),
                new Rectangle(stamp.GetLeft(), stamp.GetBottom() - Gap - h, w, h),
                new Rectangle(stamp.GetLeft(), top + Gap, w, h),
            };

            Rectangle? missesStamp = null;
            foreach (var candidate in candidates)
            {
                // Only spots that fit without moving: a shifted spot would end on the stamp.
                if (!Inside(candidate, page)) continue;
                if (Overlaps(candidate, stamp)) continue;
                missesStamp ??= candidate;
                if (!occupied.Any(o => Overlaps(candidate, o))) return candidate;
            }
            // Page too full or too small: inside the page at least, beside the stamp where it fits.
            return missesStamp ?? Clamp(candidates[0], page);
        }

        /// <summary>Signature widgets already on the page (earlier signers), in page coordinates.</summary>
        public static IReadOnlyList<Rectangle> SignatureFieldsOn(PdfDocument doc, int pageNumber)
        {
            var result = new List<Rectangle>();
            var form = PdfAcroForm.GetAcroForm(doc, false);
            if (form == null) return result;
            var page = doc.GetPage(pageNumber);
            foreach (var field in form.GetAllFormFields().Values)
            {
                if (field.GetFormType() != PdfName.Sig) continue;
                foreach (var widget in field.GetWidgets())
                {
                    if (widget.GetPage() != page) continue;
                    var rect = widget.GetRectangle()?.ToRectangle();
                    if (rect != null && rect.GetWidth() > 0 && rect.GetHeight() > 0) result.Add(rect);
                }
            }
            return result;
        }

        private static bool Inside(Rectangle r, Rectangle page) =>
            r.GetLeft() >= page.GetLeft() && r.GetBottom() >= page.GetBottom()
            && r.GetRight() <= page.GetRight() && r.GetTop() <= page.GetTop();

        private static bool Overlaps(Rectangle a, Rectangle b) =>
            a.GetLeft() < b.GetRight() && b.GetLeft() < a.GetRight()
            && a.GetBottom() < b.GetTop() && b.GetBottom() < a.GetTop();

        private static Rectangle Clamp(Rectangle r, Rectangle page)
        {
            float x = Math.Clamp(r.GetLeft(), page.GetLeft(), page.GetRight() - r.GetWidth());
            float y = Math.Clamp(r.GetBottom(), page.GetBottom(), page.GetTop() - r.GetHeight());
            return new Rectangle(x, y, r.GetWidth(), r.GetHeight());
        }
    }
}
