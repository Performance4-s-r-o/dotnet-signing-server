using iText.Kernel.Geom;
using iText.Kernel.Pdf;

namespace DotNetSigningServer.Services.SignatureLayout
{
    /// <summary>
    /// The page as the client sees it (portál #511): the CropBox turned by <c>/Rotate</c>, origin
    /// bottom left at the CropBox corner. Clients place signatures on a pdf.js viewport of exactly
    /// this, so every rect from a client is in this frame; the PDF needs default user space.
    /// One conversion for the stamp, the seal, the field and the placement check.
    /// </summary>
    public sealed class PageFrame
    {
        private PageFrame(Rectangle crop, int rotation)
        {
            Crop = crop;
            Rotation = rotation;
            bool turned = rotation is 90 or 270;
            Width = turned ? crop.GetHeight() : crop.GetWidth();
            Height = turned ? crop.GetWidth() : crop.GetHeight();
        }

        /// <summary>CropBox in default user space.</summary>
        public Rectangle Crop { get; }

        /// <summary>0, 90, 180 or 270 (clockwise when displayed).</summary>
        public int Rotation { get; }

        /// <summary>Width as displayed.</summary>
        public float Width { get; }

        /// <summary>Height as displayed.</summary>
        public float Height { get; }

        /// <summary>The visible page in the client frame.</summary>
        public Rectangle Box => new(0, 0, Width, Height);

        /// <summary>No offset and no rotation: client and PDF coordinates are the same.</summary>
        public bool IsIdentity => Rotation == 0 && Crop.GetX() == 0 && Crop.GetY() == 0;

        public static PageFrame Of(PdfPage page) => new(page.GetCropBox(), ((page.GetRotation() % 360) + 360) % 360);

        /// <summary>Unrotated page box with this origin, for callers that know no page.</summary>
        public static PageFrame Plain(Rectangle box) => new(box, 0);

        /// <summary>Client frame → default user space, as <c>a b c d e f</c> (same as pdf.js inverted).</summary>
        public float[] ToPageMatrix()
        {
            float x0 = Crop.GetX(), y0 = Crop.GetY(), w = Crop.GetWidth(), h = Crop.GetHeight();
            return Rotation switch
            {
                90 => new[] { 0f, 1f, -1f, 0f, x0 + w, y0 },
                180 => new[] { -1f, 0f, 0f, -1f, x0 + w, y0 + h },
                270 => new[] { 0f, -1f, 1f, 0f, x0, y0 + h },
                _ => new[] { 1f, 0f, 0f, 1f, x0, y0 },
            };
        }

        /// <summary>A rect of the client frame in default user space.</summary>
        public Rectangle ToPage(Rectangle r)
        {
            float x0 = Crop.GetX(), y0 = Crop.GetY(), w = Crop.GetWidth(), h = Crop.GetHeight();
            float x = r.GetX(), y = r.GetY(), rw = r.GetWidth(), rh = r.GetHeight();
            return Rotation switch
            {
                90 => new Rectangle(x0 + w - y - rh, y0 + x, rh, rw),
                180 => new Rectangle(x0 + w - x - rw, y0 + h - y - rh, rw, rh),
                270 => new Rectangle(x0 + y, y0 + h - x - rw, rh, rw),
                _ => new Rectangle(x0 + x, y0 + y, rw, rh),
            };
        }

        /// <summary>A rect of default user space in the client frame (inverse of <see cref="ToPage"/>).</summary>
        public Rectangle ToView(Rectangle r)
        {
            float x0 = Crop.GetX(), y0 = Crop.GetY(), w = Crop.GetWidth(), h = Crop.GetHeight();
            float x = r.GetX() - x0, y = r.GetY() - y0, rw = r.GetWidth(), rh = r.GetHeight();
            return Rotation switch
            {
                90 => new Rectangle(y, w - x - rw, rh, rw),
                180 => new Rectangle(w - x - rw, h - y - rh, rw, rh),
                270 => new Rectangle(h - y - rh, x, rh, rw),
                _ => new Rectangle(x, y, rw, rh),
            };
        }
    }
}
