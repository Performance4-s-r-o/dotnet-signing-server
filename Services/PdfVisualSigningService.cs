using DotNetSigningServer.Models;
using DotNetSigningServer.Services.SignatureDesign;
using DotNetSigningServer.Services.SignatureLayout;
using iText.Commons.Bouncycastle.Cert;
using iText.Forms.Fields.Properties;
using iText.Forms.Form.Element;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;
using iText.Signatures;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.Layout.Borders;

namespace DotNetSigningServer.Services
{
    public class PdfVisualSigningService
    {
        public string ApplyVisualSign(VisualSignInput input) =>
            ApplyVisualSign(input, Convert.FromBase64String(input.PdfContent));

        /// <summary>Vizuální podpis nad bajty, které už prošly vstupní kontrolou; <c>input.PdfContent</c> se nečte.</summary>
        /// <param name="keepPdfA">Set when the document was prepared as PDF/A (#184): it is written as a
        /// PDF/A document, so iText checks what is drawn (fonts, images) and keeps the metadata in step.</param>
        public string ApplyVisualSign(VisualSignInput input, byte[] pdfBytes, PdfAConformance? keepPdfA = null) =>
            Convert.ToBase64String(Draw(input, pdfBytes, keepPdfA, companyName: null).Pdf);

        /// <summary>
        /// Content stamp of presign (portál #511): the same drawing as visual-sign, with the box where it landed.
        /// </summary>
        public static (byte[] Pdf, int Page, Rectangle Rect) StampContent(ContentStampInput input, byte[] pdfBytes, PdfAConformance? keepPdfA) =>
            Draw(input.ToVisualSign(), pdfBytes, keepPdfA, CompanyOf(input));

        internal static string? CompanyOf(ContentStampInput input) =>
            string.IsNullOrWhiteSpace(input.CompanyName) ? null : input.CompanyName;

        private static (byte[] Pdf, int Page, Rectangle Rect) Draw(VisualSignInput input, byte[] pdfBytes, PdfAConformance? keepPdfA, string? companyName)
        {
            using var msIn = new MemoryStream(pdfBytes);
            using var msOut = new MemoryStream();

            var reader = new PdfReader(msIn);
            var writer = new PdfWriter(msOut);
            var stamping = new StampingProperties().UseAppendMode();
            var pdfDoc = keepPdfA != null
                ? new iText.Pdfa.PdfADocument(reader, writer, stamping)
                : new PdfDocument(reader, writer, stamping);

            var plan = PlanStamp(input, pdfDoc, companyName);
            DrawIntoContent(plan, pdfDoc);
            pdfDoc.Close();

            return (msOut.ToArray(), plan.Page, plan.Rect);
        }

        /// <summary>
        /// Preview of the appearance (#117): the page with the stamp drawn as visual-sign draws it, alone,
        /// with <paramref name="watermark"/> across it. Nothing is signed; the document is not kept as PDF/A,
        /// but its conformance is read, so a PDF/A-1 document is simplified (and warned about) as when signing.
        /// The one-page, watermarked result is no usable signed document, which is why it is free.
        /// </summary>
        public static byte[] Preview(VisualSignInput input, byte[] pdfBytes, string watermark)
        {
            using var msIn = new MemoryStream(pdfBytes);
            using var msOut = new MemoryStream();
            var pdfDoc = new PdfDocument(new PdfReader(msIn), new PdfWriter(msOut));

            var plan = PlanStamp(input, pdfDoc, companyName: null);
            DrawIntoContent(plan, pdfDoc);
            for (int i = pdfDoc.GetNumberOfPages(); i >= 1; i--)
            {
                if (i != plan.Page) pdfDoc.RemovePage(i);
            }
            DrawWatermark(pdfDoc.GetPage(1), watermark);
            pdfDoc.Close();
            return msOut.ToArray();
        }

        /// <summary>Light grey text across the visible page, over everything, upright to the reader.</summary>
        private static void DrawWatermark(PdfPage page, string text)
        {
            var frame = PageFrame.Of(page);
            var box = frame.Box;
            var font = AppFonts.Load(AppFontFamily.Sans, bold: true);
            float diagonal = (float)Math.Sqrt(box.GetWidth() * box.GetWidth() + box.GetHeight() * box.GetHeight());
            float unitWidth = font.GetWidth(text, 1);
            if (unitWidth <= 0) return;
            float size = Math.Min(diagonal * 0.6f / unitWidth, 160f);
            double angle = Math.Atan2(box.GetHeight(), box.GetWidth());
            float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
            float textWidth = unitWidth * size;
            float cx = box.GetX() + box.GetWidth() / 2, cy = box.GetY() + box.GetHeight() / 2;
            // Start so that the text's middle (and half its cap height) sits in the middle of the page.
            float x = cx - cos * textWidth / 2 + sin * size * 0.35f;
            float y = cy - sin * textWidth / 2 - cos * size * 0.35f;

            var canvas = new PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), page.GetDocument());
            canvas.SaveState();
            if (!frame.IsIdentity)
            {
                var m = frame.ToPageMatrix();
                canvas.ConcatMatrix(m[0], m[1], m[2], m[3], m[4], m[5]);
            }
            canvas.SetFillColor(new iText.Kernel.Colors.DeviceRgb(190, 190, 190));
            canvas.BeginText();
            canvas.SetFontAndSize(font, size);
            canvas.SetTextMatrix(cos, sin, -sin, cos, x, y);
            canvas.ShowText(text);
            canvas.EndText();
            canvas.RestoreState();
            canvas.Release();
        }

        /// <summary>Where a stamp lands, before anything is drawn.</summary>
        public sealed class StampPlan
        {
            internal StampPlan(VisualSignInput input, string? companyName, int page, Rectangle rect, PageFrame frame)
            {
                Input = input;
                CompanyName = companyName;
                Page = page;
                Rect = rect;
                Frame = frame;
            }

            internal VisualSignInput Input { get; }
            internal string? CompanyName { get; }
            public int Page { get; }
            /// <summary>The box in the client frame (CropBox corner, as displayed), see <see cref="PageFrame"/>.</summary>
            public Rectangle Rect { get; }
            public PageFrame Frame { get; }
            /// <summary>The box in default user space.</summary>
            public Rectangle PageRect => Frame.ToPage(Rect);
        }

        /// <summary>The stamp's box on its page: the request's <c>SignRect</c>, clamped to the visible page (CropBox, client frame).</summary>
        public static StampPlan PlanStamp(VisualSignInput input, PdfDocument pdfDoc, string? companyName)
        {
            int pageNumber = input.SignPageNumber <= 0 ? 1 : input.SignPageNumber;
            var page = pdfDoc.GetPage(pageNumber);
            var (width, height) = SignatureAppearance.Size(input.SignRect);
            var frame = PageFrame.Of(page);
            var rect = ClampToPage(new Rectangle(input.SignRect.X, input.SignRect.Y, width, height), frame.Box);
            return new StampPlan(input, companyName, pageNumber, rect, frame);
        }

        /// <summary>
        /// The stamp in the page content of a document nobody signed yet. The old content is wrapped
        /// in q/Q, so an unbalanced <c>cm</c> in it cannot move the stamp away from the seal field.
        /// </summary>
        public static void DrawIntoContent(StampPlan plan, PdfDocument pdfDoc)
        {
            var xobject = Render(plan, pdfDoc);
            var canvas = new PdfCanvas(pdfDoc.GetPage(plan.Page), true);
            canvas.SaveState();
            if (!plan.Frame.IsIdentity)
            {
                // Drawn in the client frame, so the stamp sits where it was placed and reads upright.
                var m = plan.Frame.ToPageMatrix();
                canvas.ConcatMatrix(m[0], m[1], m[2], m[3], m[4], m[5]);
            }
            canvas.AddXObjectAt(xobject, plan.Rect.GetX(), plan.Rect.GetY());
            canvas.RestoreState();
            canvas.Release();
        }

        /// <summary>
        /// The stamp as a locked <c>/Stamp</c> annotation with its own appearance, for a document that is
        /// already signed: the page content of earlier revisions stays as it was (portál #511).
        /// </summary>
        public static void AddAsAnnotation(StampPlan plan, PdfDocument pdfDoc)
        {
            var appearance = Render(plan, pdfDoc);
            if (plan.Frame.Rotation != 0)
            {
                // Turned against the page's /Rotate, so the stamp reads upright.
                var m = plan.Frame.ToPageMatrix();
                appearance.GetPdfObject().Put(PdfName.Matrix, new PdfArray(new[] { m[0], m[1], m[2], m[3], 0f, 0f }));
            }

            var annotation = new PdfStampAnnotation(plan.PageRect);
            annotation.SetStampName(new PdfName("P4PDFSignature"));
            annotation.SetName(new PdfString(SealPlacement.StampAnnotationPrefix + Guid.NewGuid().ToString("N")));
            annotation.SetNormalAppearance(appearance.GetPdfObject());
            annotation.SetFlags(PdfAnnotation.PRINT | PdfAnnotation.READ_ONLY | PdfAnnotation.LOCKED);
            annotation.SetDate(new PdfDate().GetPdfObject());
            pdfDoc.GetPage(plan.Page).AddAnnotation(annotation);
        }

        private static PdfFormXObject Render(StampPlan plan, PdfDocument pdfDoc)
        {
            var input = plan.Input;
            return SignatureAppearance.Render(pdfDoc, new SignatureAppearanceContent
            {
                Options = input.Appearance,
                SignImage = input.SignImageContent,
                LogoImage = input.CompanyLogoContent,
                StampImage = input.StampImageContent,
                BackgroundImage = input.BackgroundImageContent,
                Name = input.SignerName,
                Company = plan.CompanyName,
                Reason = input.Reason,
                Location = input.Location,
            }, plan.Rect.GetWidth(), plan.Rect.GetHeight());
        }

        /// <summary>The rect moved (and if need be shrunk) into the page box, origin included.</summary>
        internal static Rectangle ClampToPage(Rectangle r, Rectangle pageBox)
        {
            float w = Math.Min(r.GetWidth(), pageBox.GetWidth());
            float h = Math.Min(r.GetHeight(), pageBox.GetHeight());
            float x = Math.Clamp(r.GetX(), pageBox.GetLeft(), pageBox.GetRight() - w);
            float y = Math.Clamp(r.GetY(), pageBox.GetBottom(), pageBox.GetTop() - h);
            return new Rectangle(x, y, w, h);
        }

        /// <summary>
        /// Signer properties of a certificate signature or a timestamp. A visible one gets the same form
        /// XObject as a page stamp, inside a field appearance without border, margin or padding.
        /// </summary>
        public static SignerProperties BuildSignerProperties(
            PdfDocument document,
            string fieldName,
            SignRect signRect,
            string reason,
            string location,
            int pageNumber,
            string? signImageContent,
            IX509Certificate[]? chain,
            SignatureAppearanceOptions? appearanceOptions = null,
            string? stampImageContent = null,
            string? backgroundImageContent = null,
            string? companyLogoContent = null,
            bool visible = true,
            PageFrame? frame = null,
            string? signerNameOverride = null)
        {
            SignerProperties signerProperties = new SignerProperties()
                .SetFieldName(fieldName)
                .SetReason(reason)
                .SetLocation(location)
                .SetCertificationLevel(AccessPermissions.UNSPECIFIED);

            if (!visible)
            {
                return signerProperties;
            }

            var subjectDN = chain?.FirstOrDefault()?.GetSubjectDN()?.ToString();
            var cn = ExtractDnField(subjectDN, "CN=");
            var org = ExtractDnField(subjectDN, "O=");
            // Qualified EU certs often split the name: CN="Jan", GN="Jan", SN="Prasil".
            // When CN is a single token, prefer GN + SN so the signer row shows the
            // full name — matches what the verification page prints.
            if (!string.IsNullOrWhiteSpace(cn) && !cn!.Contains(' '))
            {
                var gn = ExtractDnField(subjectDN, "GN=") ?? ExtractDnField(subjectDN, "G=");
                var sn = ExtractDnField(subjectDN, "SN=");
                if (!string.IsNullOrWhiteSpace(gn) && !string.IsNullOrWhiteSpace(sn))
                {
                    cn = $"{gn} {sn}";
                }
            }
            // Client-provided display name wins when present — it comes from the
            // SSO identity (e.g. Azure AD), which is what the user expects to see.
            if (!string.IsNullOrWhiteSpace(signerNameOverride))
            {
                cn = signerNameOverride;
            }

            // The rect is in the client frame (portál #511); the field needs user space,
            // iText turns the appearance by /Rotate itself.
            var (width, height) = SignatureAppearance.Size(signRect);
            var rect = new Rectangle(signRect.X, signRect.Y, width, height);
            if (frame != null) rect = ClampToPage(rect, frame.Box);

            var xobject = SignatureAppearance.Render(document, new SignatureAppearanceContent
            {
                Options = appearanceOptions,
                SignImage = signImageContent,
                LogoImage = companyLogoContent,
                StampImage = stampImageContent,
                BackgroundImage = backgroundImageContent,
                Name = cn,
                Company = org,
                Reason = reason,
                Location = location,
            }, rect.GetWidth(), rect.GetHeight());

            // SignatureFieldAppearance is a FormField: by default it draws a border and keeps margin and
            // padding, which would inset the XObject. Strip them so it fills the field edge to edge.
            var appearance = new SignatureFieldAppearance(SignerProperties.IGNORED_ID);
            appearance.SetBorder(Border.NO_BORDER);
            var zero = UnitValue.CreatePointValue(0f);
            foreach (var p in new[] { Property.MARGIN_LEFT, Property.MARGIN_RIGHT, Property.MARGIN_TOP, Property.MARGIN_BOTTOM,
                                      Property.PADDING_LEFT, Property.PADDING_RIGHT, Property.PADDING_TOP, Property.PADDING_BOTTOM })
            {
                appearance.SetProperty(p, zero);
            }
            // PDF/A wants every font embedded, also the default one the field would otherwise carry.
            appearance.SetFont(AppFonts.Load(AppFontFamily.Sans));
            var image = new Image(xobject).SetWidth(rect.GetWidth()).SetHeight(rect.GetHeight()).SetMargins(0, 0, 0, 0).SetPadding(0);
            var div = new Div().SetWidth(rect.GetWidth()).SetHeight(rect.GetHeight()).SetMargin(0).SetPadding(0).Add(image);
            appearance.SetContent(div);

            signerProperties
                .SetPageRect(frame != null ? frame.ToPage(rect) : rect)
                .SetPageNumber(pageNumber)
                .SetSignatureAppearance(appearance);

            return signerProperties;
        }

        private static string? ExtractDnField(string? subjectDN, string prefix)
        {
            if (string.IsNullOrWhiteSpace(subjectDN) || string.IsNullOrWhiteSpace(prefix)) return null;
            var part = subjectDN
                .Split(',')
                .Select(p => p.Trim())
                .FirstOrDefault(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return part?.Substring(prefix.Length);
        }
    }
}
