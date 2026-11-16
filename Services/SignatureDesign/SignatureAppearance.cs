using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using ImageMagick;
using iText.IO.Image;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Xobject;

namespace DotNetSigningServer.Services.SignatureDesign
{
    /// <summary>What a visible signature shows, besides the model: images and the values of the text lines.</summary>
    public sealed class SignatureAppearanceContent
    {
        public SignatureAppearanceOptions? Options { get; init; }
        public string? SignImage { get; init; }
        public string? LogoImage { get; init; }
        /// <summary>Custom image of the design (the stamp of a seal), zone kind <c>image</c>.</summary>
        public string? StampImage { get; init; }
        public string? BackgroundImage { get; init; }
        public string? Name { get; init; }
        public string? Company { get; init; }
        public string? Reason { get; init; }
        public string? Location { get; init; }
        public DateTime DateUtc { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// The one way a visible signature is drawn (#116): the layout model through <see cref="SignatureLayoutEngine"/>
    /// into one <see cref="PdfFormXObject"/> by <see cref="SignatureLayoutRenderer"/>. Page stamp, stamp annotation
    /// and certificate field all use it, so they look the same as the portal's preview.
    /// </summary>
    public static class SignatureAppearance
    {
        /// <summary>The model is not valid (400).</summary>
        public const string InvalidCode = "APPEARANCE_INVALID";
        /// <summary>A seal shows a handwritten signature or the signer's name (400).</summary>
        public const string SealViolationCode = "APPEARANCE_SEAL_VIOLATION";
        /// <summary>The box is outside what the layout handles (400).</summary>
        public const string FrameInvalidCode = "SIGN_RECT_INVALID";
        /// <summary>An image of the appearance cannot be read as an image (422); never dropped silently.</summary>
        public const string ImageInvalidCode = "APPEARANCE_IMAGE_INVALID";
        /// <summary>PDF/A-1 and a background image or watermark (422).</summary>
        public const string TransparencyNotAllowedCode = SignatureLayoutRenderer.TransparencyNotAllowed;

        /// <summary>
        /// Warning: the document is PDF/A-1, so the signature was drawn without transparency — transparent
        /// images flattened, a background that needs transparency left out (decision of 16 Nov 2026).
        /// </summary>
        public const string Pdfa1SimplifiedWarning = "APPEARANCE_PDFA1_SIMPLIFIED";

        private static readonly AsyncLocal<HashSet<string>?> WarningSink = new();

        /// <summary>
        /// Starts collecting appearance warnings for the current request (flows into awaited work).
        /// Call at the start of an endpoint; read the set once the operation finished.
        /// </summary>
        public static HashSet<string> BeginWarnings()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            WarningSink.Value = set;
            return set;
        }

        private static void Warn(string code)
        {
            var set = WarningSink.Value;
            if (set == null) return;
            lock (set) set.Add(code);
        }

        /// <summary>Box used when the request gives no size (width or height 0): size M.</summary>
        public const float DefaultWidth = 200;
        public const float DefaultHeight = 70;

        /// <summary>Refusals raised while drawing, for the routes' catch clauses.</summary>
        public static readonly IReadOnlySet<string> RenderRefusalCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            ImageInvalidCode, TransparencyNotAllowedCode, FrameInvalidCode,
        };

        /// <summary>
        /// Input check before anything is done: the model, the seal rule and the size of a visible box.
        /// Returns the refusal code or <c>null</c>.
        /// </summary>
        public static string? Refusal(SignatureAppearanceOptions? options, SignRect? rect = null)
        {
            if (options?.Layout is { } layout && layout.Validate() != null) return InvalidCode;
            if (options?.Labels is { } labels
                && (Bad(labels.Reason) || Bad(labels.Location))) return InvalidCode;
            if (options?.Seal == true && ModelFor(options).SealViolation() != null) return SealViolationCode;
            if (rect != null && (!FrameOk(rect.Width) || !FrameOk(rect.Height))) return FrameInvalidCode;
            return null;

            static bool Bad(string? label) => label is { Length: > SignatureLayoutModel.PrefixMax } || label?.Any(char.IsControl) == true;
            // 0 = the default size; otherwise what the engine lays out.
            static bool FrameOk(float v) => v == 0 || (v >= SignatureLayoutEngine.MinFrame && v <= SignatureLayoutEngine.MaxFrame);
        }

        /// <summary>The request's size, the default for a missing dimension.</summary>
        public static (float Width, float Height) Size(SignRect rect) =>
            (rect.Width > 0 ? rect.Width : DefaultWidth, rect.Height > 0 ? rect.Height : DefaultHeight);

        public static SignatureLayoutModel ModelFor(SignatureAppearanceOptions? options) =>
            options?.Layout ?? (options?.Seal == true ? DefaultSeal() : new SignatureLayoutModel());

        /// <summary>Same as the portal's <c>defaultSealLayoutModel</c>: logo, text without a name, stamp on the right.</summary>
        public static SignatureLayoutModel DefaultSeal()
        {
            var model = new SignatureLayoutModel { Left = "logo", Right = "image" };
            model.Elements.Name = new SignatureTextElement();
            model.Elements.Company = new SignatureTextElement { Enabled = true, Bold = true };
            return model;
        }

        /// <summary>Signing time as the date line shows it.</summary>
        public static string FormatDate(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC";

        /// <summary>
        /// The appearance as one form XObject of <paramref name="width"/> × <paramref name="height"/> pt.
        /// In PDF/A-1 an image with an alpha channel (a drawn signature) is flattened onto the colour under it
        /// and a background that needs transparency is left out, with <see cref="Pdfa1SimplifiedWarning"/>.
        /// </summary>
        public static PdfFormXObject Render(PdfDocument doc, SignatureAppearanceContent content, double width, double height)
        {
            if (!(width >= SignatureLayoutEngine.MinFrame && width <= SignatureLayoutEngine.MaxFrame
                  && height >= SignatureLayoutEngine.MinFrame && height <= SignatureLayoutEngine.MaxFrame))
                throw new ApiValidationException(FrameInvalidCode, $"signature box {width} x {height} pt");

            var model = ModelFor(content.Options);
            static bool Has(string? base64) => !string.IsNullOrWhiteSpace(base64);

            var layout = SignatureLayoutEngine.Compute(new SignatureLayoutInput
            {
                Model = model,
                Width = width,
                Height = height,
                Values = new SignatureLayoutValues
                {
                    Name = content.Name,
                    Company = content.Company,
                    Reason = content.Reason,
                    Location = content.Location,
                    Date = FormatDate(content.DateUtc),
                },
                Labels = new SignatureLayoutLabels
                {
                    Reason = content.Options?.Labels?.Reason ?? "Reason:",
                    Location = content.Options?.Labels?.Location ?? "Location:",
                },
                Assets = new SignatureLayoutAssets
                {
                    Sig = Has(content.SignImage),
                    Logo = Has(content.LogoImage),
                    Image = Has(content.StampImage),
                    BackgroundImage = Has(content.BackgroundImage),
                },
            });

            // Only images the layout draws are decoded: one it leaves out cannot fail the signature.
            var drawn = layout.Zones.Select(z => z.Kind).ToHashSet(StringComparer.Ordinal);
            var sig = drawn.Contains("sig") ? Decode(content.SignImage, "signature") : null;
            var logo = drawn.Contains("logo") || layout.Background.Kind == "watermark" ? Decode(content.LogoImage, "logo") : null;
            var stamp = drawn.Contains("image") ? Decode(content.StampImage, "stamp") : null;
            var background = layout.Background.Kind == "image" ? Decode(content.BackgroundImage, "background") : null;

            // A CMYK JPEG does not match an sRGB output intent (the only one this server writes).
            if (doc.GetConformance().IsPdfA() && new[] { sig, logo, stamp, background }.Any(i => i?.Data.GetColorEncodingComponentsNumber() == 4))
                throw new ApiValidationException(ImageInvalidCode, "CMYK image in a PDF/A document");

            bool pdfA1 = doc.GetConformance().GetAConformance()?.GetPart() == "1";
            if (pdfA1)
            {
                // PDF/A-1 has no transparency and no 16-bit images (decision of 16 Nov: sign, do not refuse, and
                // warn). A PNG with an alpha channel or 16 bits is made 8-bit RGB: real transparency is flattened
                // onto the frame's colour, without one onto white. A background that needs transparency (a
                // translucent or transparent image or watermark, or one with a transparent image over it) is left
                // out. An alpha channel that is fully opaque (common for logos) changes nothing and warns of nothing.
                var under = layout.Background.Kind is "color" or "pattern" ? layout.Background.Color ?? "#ffffff" : "#ffffff";
                var (flatSig, sigAlpha) = Opaque(sig, under);
                var (flatLogo, logoAlpha) = Opaque(logo, under);
                var (flatStamp, stampAlpha) = Opaque(stamp, under);
                var (flatBackground, backgroundAlpha) = Opaque(background, "#ffffff");
                (sig, logo, stamp, background) = (flatSig, flatLogo, flatStamp, flatBackground);

                bool overlay = sigAlpha || logoAlpha || stampAlpha;
                var bg = layout.Background;
                bool translucent = (bg.Alpha ?? 1) < 1;
                bool dropBackground =
                    (bg.Kind == "image" && (translucent || backgroundAlpha || overlay))
                    || (bg.Kind == "watermark" && (translucent || overlay));
                if (dropBackground)
                {
                    layout.Background = new LayoutBackground { Kind = "none" };
                    background = null;
                }
                if (overlay || dropBackground) Warn(Pdfa1SimplifiedWarning);
            }

            var images = new SignatureLayoutImages
            {
                Sig = sig?.Data,
                Logo = logo?.Data,
                Image = stamp?.Data,
                Background = background?.Data,
            };
            try
            {
                return SignatureLayoutRenderer.Render(doc, layout, images, transparencyAllowed: !pdfA1);
            }
            catch (SignatureAppearanceUnsupportedException ex)
            {
                throw new ApiValidationException(ex.Code, ex.Message);
            }
        }

        private static bool IsPngOrJpeg(byte[] b) =>
            (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
            || (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF);

        /// <summary>Longest side an image with alpha keeps when it is flattened for PDF/A-1.</summary>
        public const int MaxFlattenSidePx = 2000;

        internal sealed record Decoded(byte[] Bytes, ImageData Data);

        /// <summary>Raw base64 or a data URI; anything that is not an image is refused, never left out.</summary>
        private static Decoded? Decode(string? input, string kind)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var payload = input;
            var comma = payload.IndexOf(',');
            if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0) payload = payload[(comma + 1)..];
            try
            {
                var bytes = Convert.FromBase64String(payload);
                // PNG and JPEG only, like the portal: other formats iText reads but ImageMagick may not (PDF/A-1).
                if (!IsPngOrJpeg(bytes)) throw new ApiValidationException(ImageInvalidCode, $"{kind} image: not PNG or JPEG");
                return new Decoded(bytes, ImageDataFactory.Create(bytes));
            }
            catch (Exception ex) when (ex is not ApiValidationException)
            {
                throw new ApiValidationException(ImageInvalidCode, $"{kind} image: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// The image flattened onto <paramref name="hex"/> when it has transparency; else unchanged. ImageMagick
        /// only reads here (the process policy allows writing raw grey bytes and nothing else, see
        /// <see cref="CodeScanRaster.Policy"/>): the pixels are composited by hand into raw RGB for iText.
        /// </summary>
        /// <returns>The 8-bit RGB image and whether it had a pixel that was not fully opaque.</returns>
        internal static (Decoded? Image, bool RealAlpha) Opaque(Decoded? image, string hex)
        {
            if (image == null || (image.Data.GetImageMask() == null && image.Data.GetBpc() <= 8)) return (image, false);
            try
            {
                CodeScanRaster.EnsureConfigured();
                using var magick = new MagickImage(image.Bytes);
                // Expanded to raw RGB below: a large, well-compressed PNG is scaled down first (still about
                // 450 dpi in the largest frame), so it cannot take hundreds of MB.
                if (Math.Max(magick.Width, magick.Height) > MaxFlattenSidePx)
                    magick.Resize(new MagickGeometry(MaxFlattenSidePx, MaxFlattenSidePx));
                int w = (int)magick.Width, h = (int)magick.Height;
                using var pixels = magick.GetPixelsUnsafe();
                var rgba = pixels.ToByteArray(PixelMapping.RGBA)
                    ?? throw new ApiValidationException(ImageInvalidCode, "flatten: no pixels");
                int br = Convert.ToInt32(hex.Substring(1, 2), 16), bgc = Convert.ToInt32(hex.Substring(3, 2), 16), bb = Convert.ToInt32(hex.Substring(5, 2), 16);
                var rgb = new byte[w * h * 3];
                bool realAlpha = false;
                for (int i = 0, j = 0; i + 3 < rgba.Length && j + 2 < rgb.Length; i += 4, j += 3)
                {
                    int a = rgba[i + 3];
                    realAlpha |= a < 255;
                    rgb[j] = (byte)((rgba[i] * a + br * (255 - a) + 127) / 255);
                    rgb[j + 1] = (byte)((rgba[i + 1] * a + bgc * (255 - a) + 127) / 255);
                    rgb[j + 2] = (byte)((rgba[i + 2] * a + bb * (255 - a) + 127) / 255);
                }
                return (new Decoded(image.Bytes, ImageDataFactory.Create(w, h, 3, 8, rgb, null)), realAlpha);
            }
            catch (MagickException ex)
            {
                throw new ApiValidationException(ImageInvalidCode, $"flatten: {ex.GetType().Name}");
            }
        }
    }
}
