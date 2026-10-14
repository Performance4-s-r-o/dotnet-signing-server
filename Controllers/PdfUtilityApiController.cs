using DotNetSigningServer.Data;
using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Operations;
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
                SafetyGuard.EnsureSafePdf(input.PdfContent, "PDF/A conversion");
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            try
            {
                var pageCount = CountPagesFromBase64(input.PdfContent);
                var requiredCredits = CalculateCreditsForPages(pageCount);

                // Reserved before the conversion; 402 here means it never ran.
                return await RunOperationAsync(user, "pdfa", ApiOperationService.Fingerprint("pdfa", input), requiredCredits, _ =>
                {
                    var pdfaResult = _pdfConversionService.ConvertToPdfA(input);
                    var conformance = PdfConversionService.FormatConformance(input.Conformance);
                    return Task.FromResult(new PaidResult(
                        new { result = pdfaResult, conformance },
                        pdfaResult,
                        new Dictionary<string, string> { ["X-PDF-Conformance"] = conformance }));
                });
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
                    SafetyGuard.EnsureSafePdf(input.PdfContent, "Fill PDF");
                }
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            try
            {
                var fingerprint = ApiOperationService.Fingerprint("fill-pdf", input);
                var (pdfBase64, pageCount) = await ResolvePdfForFillAsync(input, user.Id);
                if (hasTemplate)
                {
                    // A stored template skipped the check above: it is the same engine, the same PDF.
                    SafetyGuard.EnsureSafePdf(pdfBase64, "Fill PDF (template)");
                }
                var requiredCredits = CalculateCreditsForPages(pageCount) * (input.Data?.Count ?? 0);

                return await RunOperationAsync(user, "fill-pdf", fingerprint, requiredCredits, async ct =>
                    new PaidResult(await PdfTemplateService.FillAsync(input, user.Id, ct)));
            }
            catch (ApiValidationException ex)
            {
                // Validace uvnitř vyplňování (strop řádků tabulky, šablona bez
                // polí) je chyba volajícího, ne 500.
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
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
                SafetyGuard.EnsureSafePdf(input.PdfContent, "Barcode scan");
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            var plan = ParseFormats(input.CodeType);
            CodeScanRaster.ScanPages pages;
            byte[] pdfBytes;
            try
            {
                pdfBytes = Convert.FromBase64String(input.PdfContent);
                // Sizes every scanned page before anything is reserved or rendered (#71):
                // a page too large for the minimum resolution is the caller's 400, not our
                // memory.
                pages = CodeScanRaster.Plan(pdfBytes);
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }
            catch (System.FormatException)
            {
                return BadRequest(new { message = Localizer["InvalidBase64"].Value });
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Barcode scan failed");
                return SafeProblem(Localizer["ScanCodesError"], ex);
            }

            try
            {
                // Charged for the pages that are scanned, not for the whole document (#71):
                // pages past the first 30 are never rendered.
                var requiredCredits = FindCodesCredits(pages);

                return await RunOperationAsync(user, "find-codes", ApiOperationService.Fingerprint("find-codes", input), requiredCredits, async ct =>
                {
                    var results = await DetectCodesAsync(pdfBytes, pages, plan, ct);
                    // `pages`/`pagesProcessed`/`credits` let the portal bill its own tenant
                    // at the same rate without opening the PDF a second time: `credits` is
                    // computed from `pagesProcessed`. It is the base rate before any
                    // concurrency tier — the tier is this server's own throttle and must
                    // not leak into the caller's price list.
                    return new PaidResult(new
                    {
                        results,
                        pages = pages.TotalPages,
                        pagesProcessed = pages.PagesProcessed,
                        credits = requiredCredits,
                        truncated = pages.Truncated,
                    });
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
        /// A scan: which symbologies, and whether the caller named one.
        /// </summary>
        /// <param name="Specs">Symbologies to look for.</param>
        /// <param name="Guarded">The caller did NOT name a single format, so the ones
        /// with no mandatory check digit are only reported when the extra evidence is
        /// there. Naming a format is the caller saying the page holds it; a group
        /// ("1d") is not specific enough to be that claim.</param>
        internal sealed record ScanPlan(IReadOnlyList<CodeFormatSpec> Specs, bool Guarded)
        {
            public IList<BarcodeFormat> Formats =>
                Specs.Where(f => f.ReadFormat.HasValue).Select(f => f.ReadFormat!.Value).Distinct().ToList();
        }

        /// <summary>
        /// Which symbologies to look for, and how carefully.
        ///
        /// The list itself lives in <see cref="CodeFormats"/>, alongside what the
        /// engine can WRITE — those two used to be separate and disagreed, so a
        /// Code 128 this server had stamped came back as "no codes found".
        ///
        /// Every readable format is scanned for. The four without a mandatory check
        /// digit used to be excluded outright, which meant a page really carrying a
        /// Code 39 came back empty; they are included now, but guarded, because a run
        /// of regular rules decodes to a value that is not on the page. Naming a
        /// format lifts the guards.
        /// </summary>
        internal static ScanPlan ParseFormats(string codeType)
        {
            var named = CodeFormats.Find(codeType);
            if (named is { CanRead: true, ReadFormat: not null })
            {
                // The caller said what is on the page. Take them at their word.
                return new ScanPlan(new[] { named }, Guarded: false);
            }

            var group = CodeFormats.FindGroup(codeType);
            return new ScanPlan((group ?? CodeFormats.ScanDefault).ToList(), Guarded: true);
        }

        [NonAction]
        internal virtual Task<IReadOnlyList<object>> DetectCodesAsync(
            byte[] pdfBytes,
            CodeScanRaster.ScanPages pages,
            ScanPlan plan,
            CancellationToken ct) => ScanCodesAsync(pdfBytes, pages, plan, ct);

        /// <summary>Credits for a scan: the pages actually rendered, never the whole document (#71).</summary>
        internal static int FindCodesCredits(CodeScanRaster.ScanPages pages) =>
            CalculateCreditsForPages(pages.PagesProcessed);

        internal static async Task<IReadOnlyList<object>> ScanCodesAsync(
            byte[] pdfBytes,
            CodeScanRaster.ScanPages pages,
            ScanPlan plan,
            CancellationToken ct)
        {
            var formats = plan.Formats;
            // Which spec produced a hit, so the guards can be applied to it by name.
            var specByFormat = plan.Specs
                .Where(f => f.ReadFormat.HasValue)
                .ToDictionary(f => f.ReadFormat!.Value, f => f);

            var results = new List<object>();
            var seen = new HashSet<string>();

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
                    UseCode39ExtendedMode = true,
                    // Code 39's check digit is optional in the standard, so demanding
                    // it is a choice — and the right one whenever nobody named the
                    // format. It rejects the READ, not the result, which is stronger
                    // than any filter downstream: a stray bar pattern has to also
                    // satisfy modulo 43, which most do not.
                    AssumeCode39CheckDigit = CodeFormats.RequiresCode39CheckDigit(plan.Specs, plan.Guarded),
                },
                AutoRotate = true,
            };

            // One page at a time (#71): rendered, decoded and released before the next,
            // so memory is one page, not thirty.
            for (var pageIndex = 0; pageIndex < pages.PagesProcessed; pageIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var dpi = pages.Dpi[pageIndex];
                var decodedOnPage = await CodeScanRaster.WithPageAsync(
                    pdfBytes, pageIndex, dpi, page => DecodePage(page, reader, ct), ct);

                foreach (var (code, pageHeightPx) in decodedOnPage)
                {
                    var text = code.Text ?? "";
                    var format = code.BarcodeFormat.ToString();

                    // Deduplicate on the VALUE and page, not on coordinates.
                    // The same code is found again in the other variants, where
                    // its points may land at slightly different numbers.
                    if (!seen.Add($"{pageIndex + 1}|{format}|{text}"))
                        continue;

                    // Length floor for the symbologies with no check digit of
                    // their own, and only when nobody asked for them. Noise
                    // decodes short; a real shipping or membership number does
                    // not. It narrows the odds, it does not close them — which
                    // is why naming the format is what lifts it.
                    if (plan.Guarded &&
                        specByFormat.TryGetValue(code.BarcodeFormat, out var spec) &&
                        !CodeFormats.IsPlausibleWhenUnnamed(spec, text))
                    {
                        continue;
                    }

                    var box = ToPdfBoundingBox(code.ResultPoints, 1.0, pageHeightPx, dpi);
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

            return results;
        }

        /// <summary>
        /// Every code on one rendered page, from three variants decoded one after another
        /// (#71; there used to be up to seven held at once). Grey as rendered; grey with
        /// contrast; that one thresholded. The sharpened copy and the 1.5×/2× enlargements
        /// are gone: the sharpen cost more CPU than any other step, and the enlargements
        /// only ran on renders under 2000 px — at 300 DPI that is pages smaller than A5,
        /// and four times the pixels of the page each. At most two images are alive at once.
        /// </summary>
        private static List<(Result Code, double PageHeightPx)> DecodePage(
            MagickImage page, BarcodeReaderGeneric reader, CancellationToken ct)
        {
            var found = new List<(Result, double)>();
            var heightPx = (double)page.Height;

            void Decode(IMagickImage<ushort> variant)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var r in TryDecodeVariant(variant, reader)) found.Add((r, heightPx));
            }

            Decode(page);
            using var contrasted = (MagickImage)page.Clone();
            contrasted.Contrast();
            Decode(contrasted);
            // In place: the contrasted copy is not needed once it has been read.
            contrasted.Threshold(new Percentage(60));
            Decode(contrasted);
            return found;
        }

        /// <summary>Rectangle on a page, in PDF points, origin bottom-left.</summary>
        public sealed record CodeBoundingBox(double X, double Y, double Width, double Height);

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
        /// 3. **Pixels to points, and flip the Y axis.** The raster is <paramref name="dpi"/>
        ///    (300, lower for large pages since #71) with
        ///    Y growing downwards; PDF is 72 points to the inch with Y growing up
        ///    from the bottom. Reporting raster pixels as if they were PDF units put
        ///    the answer roughly four times too far, upside down.
        /// </summary>
        internal static CodeBoundingBox? ToPdfBoundingBox(ResultPoint[]? points, double scale, double pageHeightPx, double dpi = CodeScanRaster.TargetDpi)
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

            var pxToPt = 72.0 / dpi;
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

        private static List<Result> TryDecodeVariant(IMagickImage<ushort> variant, BarcodeReaderGeneric reader)
        {
            // One byte per pixel: the page is grey already, and RGBA was four times the
            // memory for the same luminance.
            var gray = variant.ToByteArray(MagickFormat.Gray);

            var luminance = new RGBLuminanceSource(
                gray,
                (int)variant.Width,
                (int)variant.Height,
                RGBLuminanceSource.BitmapFormat.Gray8
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
