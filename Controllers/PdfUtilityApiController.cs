using DotNetSigningServer.Data;
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
        private readonly PdfConversionService _pdfConversionService;
        private readonly FlowPipelineService _flowPipelineService;

        public PdfUtilityApiController(
            ApplicationDbContext dbContext,
            IApiAuthService apiAuthService,
            ILogger<PdfUtilityApiController> logger,
            ContentLimitGuard limitGuard,
            IOptions<BillingOptions> billingOptions,
            IWebHostEnvironment env,
            PdfTemplateService pdfTemplateService,
            PdfConversionService pdfConversionService,
            FlowPipelineService flowPipelineService,
            IStringLocalizer<SharedStrings> localizer)
            : base(dbContext, apiAuthService, logger, limitGuard, billingOptions, env, pdfTemplateService, localizer)
        {
            _pdfConversionService = pdfConversionService;
            _flowPipelineService = flowPipelineService;
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
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            try
            {
                var pageCount = CountPagesFromBase64(input.PdfContent);
                var requiredCredits = CalculateCreditsForPages(pageCount);
                if (requiredCredits > 0 && user.CreditsRemaining < requiredCredits)
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
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            try
            {
                var (pdfBase64, pageCount) = await ResolvePdfForFillAsync(input, user.Id);
                var requiredCredits = CalculateCreditsForPages(pageCount) * (input.Data?.Count ?? 0);
                if (requiredCredits > 0 && user.CreditsRemaining < requiredCredits)
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
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            var formats = ParseFormats(input.CodeType);
            try
            {
                var pageCount = CountPagesFromBase64(input.PdfContent);
                var requiredCredits = CalculateCreditsForPages(pageCount);
                if (requiredCredits > 0 && user.CreditsRemaining < requiredCredits)
                {
                    return PaymentRequired(user, requiredCredits);
                }

                var results = await DetectCodesAsync(input.PdfContent, formats);
                await DebitUserAsync(user, requiredCredits);
                return Ok(new { results });
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Barcode scan failed");
                return SafeProblem(Localizer["ScanCodesError"], ex);
            }
        }

        [HttpPost("/api/flow")]
        public async Task<IActionResult> StartFlow([FromBody] FlowPipelineInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if ((input.PdfContents == null || input.PdfContents.Count == 0) && input.FillPdf == null)
            {
                return BadRequest(new { message = Localizer["ProvidePdfOrFillPdf"].Value });
            }

            if (input.Flow == null || input.Flow.Count == 0)
            {
                return BadRequest(new { message = Localizer["FlowRequired"].Value });
            }

            var invalid = input.Flow.FirstOrDefault(f =>
                string.IsNullOrWhiteSpace(f.Action) ||
                !IsAllowedFlowAction(f.Action));
            if (invalid != null)
            {
                return BadRequest(new { message = Localizer["UnsupportedFlowAction", invalid.Action ?? ""].Value });
            }

            var terminalActions = input.Flow.Count(f => IsTerminalAction(f.Action));
            if (terminalActions > 1)
            {
                return BadRequest(new { message = Localizer["OnlyOneSigningAction"].Value });
            }

            int requiredCredits;
            try
            {
                requiredCredits = await CalculateCreditsForFlowAsync(input, user.Id);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            if (requiredCredits > 0 && user.CreditsRemaining < requiredCredits)
            {
                return PaymentRequired(user, requiredCredits);
            }

            try
            {
                var flowId = await _flowPipelineService.StartFlowAsync(user.Id, input, HttpContext.RequestAborted);
                if (requiredCredits > 0)
                {
                    await DebitUserAsync(user, requiredCredits);
                }
                return Ok(new { id = flowId, status = "inprogress" });
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Flow start failed");
                return SafeProblem(Localizer["FlowStartError"], ex);
            }
        }

        [HttpGet("/api/flow/{flowId:guid}")]
        public async Task<IActionResult> GetFlowStatus(Guid flowId)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                var status = await _flowPipelineService.GetStatusAsync(flowId, user.Id, HttpContext.RequestAborted);
                return Ok(status);
            }
            catch (Exception ex)
            {
                return NotFound(new { message = ex.Message });
            }
        }

        [HttpPost("/api/flow-sign")]
        public async Task<IActionResult> CompleteFlowSignatures([FromBody] FlowSignRequest request)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (request.Signatures == null || request.Signatures.Count == 0)
            {
                return BadRequest(new { message = Localizer["SignaturesRequired"].Value });
            }

            try
            {
                var status = await _flowPipelineService.CompleteSignaturesAsync(request.FlowId, user.Id, request.Signatures, HttpContext.RequestAborted);
                return Ok(status);
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Flow sign failed");
                return SafeProblem(Localizer["FlowSignaturesError"], ex);
            }
        }

        #region Private helpers

        private static IList<BarcodeFormat> ParseFormats(string codeType)
        {
            var formats = new List<BarcodeFormat>();
            var normalized = (codeType ?? "any").Trim().ToLowerInvariant();
            if (normalized == "qr")
            {
                formats.Add(BarcodeFormat.QR_CODE);
            }
            else if (normalized is "datamatrix" or "data-matrix" or "dm")
            {
                formats.Add(BarcodeFormat.DATA_MATRIX);
            }
            else if (normalized == "pdf417")
            {
                formats.Add(BarcodeFormat.PDF_417);
            }
            else if (normalized == "aztec")
            {
                formats.Add(BarcodeFormat.AZTEC);
            }
            else
            {
                formats.AddRange(new[]
                {
                    BarcodeFormat.QR_CODE,
                    BarcodeFormat.DATA_MATRIX,
                    BarcodeFormat.PDF_417,
                    BarcodeFormat.AZTEC
                });
            }

            return formats;
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
                Format = MagickFormat.Pdf
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

                try
                {
                    foreach (var variant in variants)
                    {

                        var decoded = TryDecodeVariant(variant, reader);
                        if (decoded.Count == 0) continue;

                        foreach (var code in decoded)
                        {
                            var text = code.Text ?? "";
                            var format = code.BarcodeFormat.ToString();
                            var key = $"{pageIndex + 1}|{format}|{text}|{code.ResultPoints?.FirstOrDefault()?.X}|{code.ResultPoints?.FirstOrDefault()?.Y}";

                            if (!seen.Add(key))
                                continue;

                            var point = code.ResultPoints?.FirstOrDefault();
                            results.Add(new
                            {
                                value = text,
                                codeType = format,
                                position = new
                                {
                                    x = point?.X ?? 0,
                                    y = point?.Y ?? 0
                                },
                                page = pageIndex + 1
                            });
                        }
                    }
                }
                finally
                {
                    foreach (var v in variants)
                        v.Dispose();

                    baseVariant.Dispose();
                }
            }

            return results;
        }

        private static List<IMagickImage> CreateVariants(IMagickImage baseVariant)
        {
            var variants = new List<IMagickImage>();

            variants.Add(((MagickImage)baseVariant).Clone());

            var gray = ((MagickImage)baseVariant).Clone();
            gray.ColorType = ColorType.Grayscale;
            gray.Contrast();
            variants.Add(gray);

            var sharpen = gray.Clone();
            sharpen.AdaptiveSharpen();
            variants.Add(sharpen);

            if (baseVariant.Width < 2000 || baseVariant.Height < 2000)
            {
                var scaled1 = gray.Clone();
                scaled1.Resize((uint)(baseVariant.Width * 1.5), (uint)(baseVariant.Height * 1.5));
                variants.Add(scaled1);

                var scaled2 = gray.Clone();
                scaled2.Resize((uint)(baseVariant.Width * 2.0), (uint)(baseVariant.Height * 2.0));
                variants.Add(scaled2);
            }

            var threshold = gray.Clone();
            threshold.Threshold(new Percentage(60));
            variants.Add(threshold);

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

        private static bool IsAllowedFlowAction(string action)
        {
            var a = (action ?? string.Empty).Trim().ToLowerInvariant();
            return a is "pdfa" or "attachment" or "presign" or "timestamp" or "sign-pfx";
        }

        private static bool IsTerminalAction(string action)
        {
            var a = (action ?? string.Empty).Trim().ToLowerInvariant();
            return a is "presign" or "timestamp" or "sign-pfx";
        }

        private async Task<int> CalculateCreditsForFlowAsync(FlowPipelineInput input, Guid userId)
        {
            var flowCredits = input.Flow?.Count ?? 0;

            var fillCredits = 0;
            if (input.FillPdf != null)
            {
                int pageCount;
                if (input.FillPdf.TemplateId != null)
                {
                    var template = await PdfTemplateService.GetTemplateAsync(input.FillPdf.TemplateId.Value, userId);
                    pageCount = CountPagesFromBase64(template.PdfContent);
                }
                else if (!string.IsNullOrWhiteSpace(input.FillPdf.PdfContent))
                {
                    pageCount = CountPagesFromBase64(input.FillPdf.PdfContent);
                }
                else
                {
                    throw new InvalidOperationException("FillPdf requires TemplateId or PdfContent.");
                }

                var dataSets = input.FillPdf.Data?.Count ?? 0;
                fillCredits = CalculateCreditsForPages(pageCount) * Math.Max(1, dataSets);
            }

            return flowCredits + fillCredits;
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
