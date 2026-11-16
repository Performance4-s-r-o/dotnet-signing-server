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
        /// In PDF/A-1 an image with an alpha channel (a drawn signature) is flattened onto the colour under it;
        /// a background image or watermark is refused (<see cref="TransparencyNotAllowedCode"/>).
        /// </summary>
        public static PdfFormXObject Render(PdfDocument doc, SignatureAppearanceContent content, double width, double height)
        {
            if (!(width >= SignatureLayoutEngine.MinFrame && width <= SignatureLayoutEngine.MaxFrame
                  && height >= SignatureLayoutEngine.MinFrame && height <= SignatureLayoutEngine.MaxFrame))
                throw new ApiValidationException(FrameInvalidCode, $"signature box {width} x {height} pt");

            var model = ModelFor(content.Options);
            var sig = Decode(content.SignImage, "signature");
            var logo = Decode(content.LogoImage, "logo");
            var stamp = Decode(content.StampImage, "stamp");
            var background = Decode(content.BackgroundImage, "background");

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
                    Sig = sig != null,
                    Logo = logo != null,
                    Image = stamp != null,
                    BackgroundImage = background != null,
                },
            });

            bool pdfA1 = doc.GetConformance().GetAConformance()?.GetPart() == "1";
            if (pdfA1)
            {
                // Under a zone lies the frame's background colour; without one, white paper.
                var under = layout.Background.Kind is "color" or "pattern" ? layout.Background.Color ?? "#ffffff" : "#ffffff";
                sig = Opaque(sig, under);
                logo = Opaque(logo, under);
                stamp = Opaque(stamp, under);
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

        private sealed record Decoded(byte[] Bytes, ImageData Data);

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
                return new Decoded(bytes, ImageDataFactory.Create(bytes));
            }
            catch (Exception ex)
            {
                throw new ApiValidationException(ImageInvalidCode, $"{kind} image: {ex.GetType().Name}");
            }
        }

        /// <summary>The image flattened onto <paramref name="hex"/> when it has transparency; else unchanged.</summary>
        private static Decoded? Opaque(Decoded? image, string hex)
        {
            if (image == null || image.Data.GetImageMask() == null) return image;
            try
            {
                using var magick = new MagickImage(image.Bytes);
                magick.BackgroundColor = new MagickColor(hex);
                magick.Alpha(AlphaAction.Remove);
                magick.Alpha(AlphaAction.Off);
                magick.Format = MagickFormat.Png24;
                var bytes = magick.ToByteArray();
                var data = ImageDataFactory.Create(bytes);
                // Still masked (unexpected): the renderer refuses it with a code instead of a broken PDF/A.
                return new Decoded(bytes, data);
            }
            catch (MagickException ex)
            {
                throw new ApiValidationException(ImageInvalidCode, $"flatten: {ex.GetType().Name}");
            }
        }
    }
}
