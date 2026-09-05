using DotNetSigningServer.Data;
using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using ImageMagick;
using ZXing;
using ZXing.Common;

namespace DotNetSigningServer.Controllers
{
    [Route("api")]
    public class PdfUtilityApiController : ApiControllerBase
    {
        // Max PDF pages rasterized by /api/find-codes (DoS guard — see usage).
        private const int MaxFindCodesRasterPages = 30;

        private readonly PdfConversionService _pdfConversionService;

        public PdfUtilityApiController(
            ApplicationDbContext dbContext,
            IApiAuthService apiAuthService,
            ILogger<PdfUtilityApiController> logger,
            ContentLimitGuard limitGuard,
            IOptions<BillingOptions> billingOptions,
            IWebHostEnvironment env,
            PdfTemplateService pdfTemplateService,
            PdfConversionService pdfConversionService,
            IStringLocalizer<SharedStrings> localizer)
            : base(dbContext, apiAuthService, logger, limitGuard, billingOptions, env, pdfTemplateService, localizer)
        {
            _pdfConversionService = pdfConversionService;
        }

        [HttpPost("/api/convert/pdfa")]
        public async Task<IActionResult> ConvertToPdfA([FromBody] ConvertToPdfAInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (string.IsNullOrWhiteSpace(input.PdfContent))
            {
                return BadRequest(new { message = Localizer["PdfContentRequired"].Value });
            }

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "PDF/A conversion");
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            try
            {
                var pageCount = CountPagesFromBase64(input.PdfContent);
                var requiredCredits = CalculateCreditsForPages(pageCount);
                if (LacksCredits(user, requiredCredits))
                {
                    return PaymentRequired(user, requiredCredits);
                }

                var pdfaResult = _pdfConversionService.ConvertToPdfA(input);
                if (requiredCredits > 0)
                {
                    await DebitUserAsync(user, requiredCredits);
                }

                var conformance = PdfConversionService.FormatConformance(input.Conformance);
                return PdfOrJsonResult(
                    pdfaResult,
                    jsonBody: new { result = pdfaResult, conformance },
                    onPdfResponse: resp => resp.Headers["X-PDF-Conformance"] = conformance);
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "PDF/A conversion failed");
                return SafeProblem(Localizer["PdfaConversionError"], ex);
            }
        }

        [HttpPost("/api/fill-pdf")]
        public async Task<IActionResult> FillPdf([FromBody] FillPdfInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            var hasTemplate = input.TemplateId != null;
            var hasContent = !string.IsNullOrWhiteSpace(input.PdfContent);

            if (hasTemplate == hasContent)
            {
                return BadRequest(new { message = Localizer["ProvideTemplateOrPdf"].Value });
            }
            if (!hasTemplate && (input.Fields == null || input.Fields.Count == 0))
            {
                return BadRequest(new { message = Localizer["FieldsRequiredForDirectPdf"].Value });
            }
            if (input.Data == null || input.Data.Count == 0)
            {
                return BadRequest(new { message = Localizer["DataSetRequired"].Value });
            }

            try
            {
                if (hasContent)
                {
                    LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Fill PDF");
                }
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            try
            {
                var (pdfBase64, pageCount) = await ResolvePdfForFillAsync(input, user.Id);
                var requiredCredits = CalculateCreditsForPages(pageCount) * (input.Data?.Count ?? 0);
                if (LacksCredits(user, requiredCredits))
                {
                    return PaymentRequired(user, requiredCredits);
                }

                var response = await PdfTemplateService.FillAsync(input, user.Id);
                if (requiredCredits > 0)
                {
                    await DebitUserAsync(user, requiredCredits);
                }
                return Ok(response);
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Fill PDF failed");
                return SafeProblem(Localizer["FillPdfError"], ex);
            }
        }

        /// <summary>
        /// Which symbologies this engine knows, and which direction each one works in.
        ///
        /// Published so the portal and the SharePoint extension can offer exactly what
        /// the engine supports rather than each keeping a guess. The three lists used
        /// to disagree: the builder offered QR, Data Matrix and "barcode" and collapsed
        /// everything else to Code 128 on save, quietly changing the symbology of a
        /// template somebody had already set up.
        ///
        /// Open on purpose — it is a capability list, not data. Requiring a key would
        /// mean the extension could not render its dropdown before the user is set up.
        /// </summary>
        [HttpGet("/api/code-formats")]
        public IActionResult GetCodeFormats()
        {
            return Ok(new
            {
                formats = CodeFormats.All.Select(f => new
                {
                    id = f.Id,
                    aliases = f.Aliases,
                    dimension = f.Dimension == CodeDimension.TwoD ? "2d" : "1d",
                    canWrite = f.CanWrite,
                    canRead = f.CanRead,
                    selfChecking = f.SelfChecking,
                    maxLength = f.MaxLength,
                }),
            });
        }

        [HttpPost("/api/find-codes")]
        public async Task<IActionResult> FindCodes([FromBody] FindCodesInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (string.IsNullOrWhiteSpace(input.PdfContent))
            {
                return BadRequest(new { message = Localizer["PdfContentRequired"].Value });
            }

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Barcode scan");
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            var formats = ParseFormats(input.CodeType);
            try
            {
                var pageCount = CountPagesFromBase64(input.PdfContent);
                var requiredCredits = CalculateCreditsForPages(pageCount);
                if (LacksCredits(user, requiredCredits))
                {
                    return PaymentRequired(user, requiredCredits);
                }

                var results = await DetectCodesAsync(input.PdfContent, formats);
                await DebitUserAsync(user, requiredCredits, operation: "find-codes");
                // `pages`/`credits` let the portal bill its own tenant at the same
                // rate without opening the PDF a second time. `credits` is the base
                // rate before any concurrency tier — the tier is this server's own
                // throttle and must not leak into the caller's price list.
                return Ok(new
                {
                    results,
                    pages = pageCount,
                    credits = requiredCredits,
                    truncated = pageCount > MaxFindCodesRasterPages,
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Barcode scan failed");
                return SafeProblem(Localizer["ScanCodesError"], ex);
            }
        }

        #region Private helpers

        /// <summary>
        /// Which symbologies to look for.
        ///
        /// The list itself lives in <see cref="CodeFormats"/>, alongside what the
        /// engine can WRITE — those two used to be separate and disagreed, so a
        /// Code 128 this server had stamped came back as "no codes found".
        ///
        /// An unnamed scan covers the self-checking formats only. The rest decode
        /// noise: on a 300 DPI render of an ordinary page a table rule reads as a
        /// Code 39 value that is not there. Naming one, or asking for "1d", is the
        /// caller saying the page really does hold them.
        /// </summary>
        internal static IList<BarcodeFormat> ParseFormats(string codeType)
        {
            var named = CodeFormats.Find(codeType);
            if (named is { CanRead: true, ReadFormat: not null })
            {
                return new List<BarcodeFormat> { named.ReadFormat.Value };
            }

            var group = CodeFormats.FindGroup(codeType);
            var specs = group ?? CodeFormats.ScanDefault;

            return specs
                .Where(f => f.ReadFormat.HasValue)
                .Select(f => f.ReadFormat!.Value)
                .Distinct()
                .ToList();
        }

        [NonAction]
        public async Task<IReadOnlyList<object>> DetectCodesAsync(
            string base64Pdf,
            IEnumerable<BarcodeFormat> formats)
        {
            byte[] pdfBytes;
            try
            {
                pdfBytes = Convert.FromBase64String(base64Pdf);
            }
            catch
            {
                throw new InvalidOperationException(Localizer["InvalidBase64"].Value);
            }

            var results = new List<object>();
            var seen = new HashSet<string>();

            using var collection = new MagickImageCollection();
            var settings = new MagickReadSettings
            {
                Density = new Density(300, 300),
                Format = MagickFormat.Pdf,
                // DoS guard: only rasterize the first N pages. A 300-DPI render of
                // every page (× several variants) of a 20 MB PDF can exhaust
                // memory/CPU; barcodes are realistically on the leading pages.
                FrameCount = MaxFindCodesRasterPages,
            };

            using (var ms = new MemoryStream(pdfBytes))
            {
                await collection.ReadAsync(ms, settings);
            }

            var reader = new BarcodeReaderGeneric(
            reader: null,
            createBinarizer: source => new HybridBinarizer(source),
            createRGBLuminanceSource: null)
            {
                Options = new DecodingOptions
                {
                    PossibleFormats = formats.ToList(),
                    TryHarder = true,
                    TryInverted = true,
                    PureBarcode = false,
                    ReturnCodabarStartEnd = true,
                    UseCode39ExtendedMode = true
                },
                AutoRotate = true,
            };

            for (var pageIndex = 0; pageIndex < collection.Count; pageIndex++)
            {
                var page = collection[pageIndex];

                var baseVariant = new MagickImage(page)
                {
                    ColorSpace = ColorSpace.sRGB,
                    Depth = 8
                };
                baseVariant.Alpha(AlphaOption.Remove);

                var variants = CreateVariants(baseVariant);
                var pageHeightPx = (double)baseVariant.Height;

                try
                {
                    foreach (var variant in variants)
                    {

                        var decoded = TryDecodeVariant(variant.Image, reader);
                        if (decoded.Count == 0) continue;

                        foreach (var code in decoded)
                        {
                            var text = code.Text ?? "";
                            var format = code.BarcodeFormat.ToString();

                            // Deduplicate on the VALUE and page, not on coordinates.
                            // The same code is found again in the enlarged variants,
                            // where its points land at different numbers, so a
                            // coordinate in the key let every code through several
                            // times over.
                            if (!seen.Add($"{pageIndex + 1}|{format}|{text}"))
                                continue;

                            var box = ToPdfBoundingBox(code.ResultPoints, variant.Scale, pageHeightPx);
                            results.Add(new
                            {
                                value = text,
                                codeType = format,
                                // Where the code is, in PDF points with the origin at
                                // the bottom-left corner — the same units and the same
                                // corner the rest of the API places things in. Null when
                                // the decoder returned no usable points; a made-up zero
                                // would read as "top-left corner of the page".
                                boundingBox = box,
                                // Kept for callers written against the old shape. It is
                                // the bottom-left of the box, no longer a raw raster
                                // pixel.
                                position = box == null ? null : new { x = box.X, y = box.Y },
                                page = pageIndex + 1
                            });
                        }
                    }
                }
                finally
                {
                    foreach (var v in variants)
                        v.Image.Dispose();

                    baseVariant.Dispose();
                }
            }

            return results;
        }

        /// <summary>
        /// One rendering of a page, plus what was done to it.
        ///
        /// The scale matters because coordinates come back in the pixel space of
        /// whichever variant happened to decode. Two of these are enlarged copies,
        /// so a point found there is 1.5× or 2× off from the page unless it is
        /// divided back — which is why the old single-point position could not be
        /// trusted even before it was converted to PDF units.
        /// </summary>
        private sealed record CodeVariant(IMagickImage Image, double Scale);

        /// <summary>Rectangle on a page, in PDF points, origin bottom-left.</summary>
        public sealed record CodeBoundingBox(double X, double Y, double Width, double Height);

        /// <summary>Points per inch of the raster the codes are decoded from.</summary>
        private const double FindCodesRasterDpi = 300.0;

        /// <summary>
        /// Decoder points to a rectangle on the page.
        ///
        /// Three conversions, and leaving any of them out is what made the old
        /// value unusable:
        ///
        /// 1. **All the points, not the first one.** ZXing returns two to four
        ///    corners; one of them is a corner of the code, which on its own says
        ///    nothing about where the code ends or how big it is.
        /// 2. **Undo the variant's enlargement.** A code found in the 2× copy is at
        ///    twice the coordinates of the page.
        /// 3. **Pixels to points, and flip the Y axis.** The raster is 300 DPI with
        ///    Y growing downwards; PDF is 72 points to the inch with Y growing up
        ///    from the bottom. Reporting raster pixels as if they were PDF units put
        ///    the answer roughly four times too far, upside down.
        /// </summary>
        internal static CodeBoundingBox? ToPdfBoundingBox(ResultPoint[]? points, double scale, double pageHeightPx)
        {
            if (points == null || points.Length == 0) return null;

            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            foreach (var point in points)
            {
                if (point == null) continue;
                var x = point.X / scale;
                var y = point.Y / scale;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
            if (minX > maxX) return null;

            const double pxToPt = 72.0 / FindCodesRasterDpi;
            var pageHeightPt = pageHeightPx * pxToPt;

            var left = minX * pxToPt;
            var right = maxX * pxToPt;
            // maxY is the LOWEST point on the page, so it becomes the bottom edge
            // once the axis is flipped.
            var bottom = pageHeightPt - (maxY * pxToPt);
            var top = pageHeightPt - (minY * pxToPt);

            return new CodeBoundingBox(
                Math.Round(left, 2),
                Math.Round(bottom, 2),
                Math.Round(right - left, 2),
                Math.Round(top - bottom, 2));
        }

        private static List<CodeVariant> CreateVariants(IMagickImage baseVariant)
        {
            var variants = new List<CodeVariant>();

            variants.Add(new CodeVariant(((MagickImage)baseVariant).Clone(), 1.0));

            var gray = ((MagickImage)baseVariant).Clone();
            gray.ColorType = ColorType.Grayscale;
            gray.Contrast();
            variants.Add(new CodeVariant(gray, 1.0));

            var sharpen = gray.Clone();
            sharpen.AdaptiveSharpen();
            variants.Add(new CodeVariant(sharpen, 1.0));

            if (baseVariant.Width < 2000 || baseVariant.Height < 2000)
            {
                var scaled1 = gray.Clone();
                scaled1.Resize((uint)(baseVariant.Width * 1.5), (uint)(baseVariant.Height * 1.5));
                variants.Add(new CodeVariant(scaled1, 1.5));

                var scaled2 = gray.Clone();
                scaled2.Resize((uint)(baseVariant.Width * 2.0), (uint)(baseVariant.Height * 2.0));
                variants.Add(new CodeVariant(scaled2, 2.0));
            }

            var threshold = gray.Clone();
            threshold.Threshold(new Percentage(60));
            variants.Add(new CodeVariant(threshold, 1.0));

            return variants;
        }

        private static List<Result> TryDecodeVariant(IMagickImage variant, BarcodeReaderGeneric reader)
        {
            var rgba = variant.ToByteArray(MagickFormat.Rgba);

            var luminance = new RGBLuminanceSource(
                rgba,
                (int)variant.Width,
                (int)variant.Height,
                RGBLuminanceSource.BitmapFormat.RGBA32
            );

            var decodedMultiple = reader.DecodeMultiple(luminance);
            if (decodedMultiple is { Length: > 0 })
            {
                return decodedMultiple.ToList();
            }

            var single = reader.Decode(luminance);
            return single != null ? new List<Result> { single } : new List<Result>();
        }

        private async Task<(string pdfBase64, int pageCount)> ResolvePdfForFillAsync(FillPdfInput input, Guid userId)
        {
            if (input.TemplateId != null)
            {
                var template = await PdfTemplateService.GetTemplateAsync(input.TemplateId.Value, userId);
                var pages = CountPagesFromBase64(template.PdfContent);
                return (template.PdfContent, pages);
            }

            var count = CountPagesFromBase64(input.PdfContent);
            return (input.PdfContent, count);
        }

        #endregion
    }
}
