using DotNetSigningServer.Services.Ai;
using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Filters;
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

namespace DotNetSigningServer.Controllers
{
    [Route("api")]
    public class PdfTemplateApiController : ApiControllerBase
    {
        private readonly TemplateAiService _templateAiService;

        public PdfTemplateApiController(
            ApplicationDbContext dbContext,
            IApiAuthService apiAuthService,
            ILogger<PdfTemplateApiController> logger,
            ContentLimitGuard limitGuard,
            IOptions<BillingOptions> billingOptions,
            IWebHostEnvironment env,
            PdfTemplateService pdfTemplateService,
            TemplateAiService templateAiService,
            IStringLocalizer<SharedStrings> localizer)
            : base(dbContext, apiAuthService, logger, limitGuard, billingOptions, env, pdfTemplateService, localizer)
        {
            _templateAiService = templateAiService;
        }

        [HttpPost("/api/pdf-template")]
        [AllowCookieSession] // template builder (Views/Home/TemplateBuilder.cshtml)
        public async Task<IActionResult> CreatePdfTemplate([FromBody] CreateTemplateInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (input.Fields == null || input.Fields.Count == 0)
            {
                return BadRequest(new { message = Localizer["FieldsRequired"].Value });
            }
            if (string.IsNullOrWhiteSpace(input.PdfContent))
            {
                return BadRequest(new { message = Localizer["PdfContentRequired"].Value });
            }

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Template");
                SafetyGuard.EnsureSafePdf(input.PdfContent, "Template");
                var response = await PdfTemplateService.CreateTemplateAsync(input, user.Id);
                return Ok(response);
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Create template failed");
                return SafeProblem(Localizer["TemplateCreateError"], ex);
            }
        }

        [HttpGet("/api/pdf-template")]
        public async Task<IActionResult> ListPdfTemplates()
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            var templates = await PdfTemplateService.ListTemplatesAsync(user.Id);
            return Ok(new { templates });
        }

        [HttpGet("/api/pdf-template/{templateId:guid}")]
        [AllowCookieSession] // template builder (Views/Home/TemplateBuilder.cshtml)
        [ProducesResponseType(typeof(TemplateDetail), StatusCodes.Status200OK)]
        public async Task<ActionResult<TemplateDetail>> GetPdfTemplate(Guid templateId)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                var template = await PdfTemplateService.GetTemplateAsync(templateId, user.Id);
                return Ok(template);
            }
            catch (ApiValidationException ex) when (ex.Code == TemplateNotFoundCode)
            {
                return TemplateNotFound();
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Get template failed");
                return SafeProblem(Localizer["TemplateRetrieveError"], ex);
            }
        }

        [HttpPut("/api/pdf-template/{templateId:guid}")]
        [AllowCookieSession] // template builder (Views/Home/TemplateBuilder.cshtml)
        public async Task<IActionResult> UpdatePdfTemplate(Guid templateId, [FromBody] UpdateTemplateInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (input.Fields != null && input.Fields.Count == 0 && string.IsNullOrWhiteSpace(input.PdfContent) && input.TemplateName == null)
            {
                return BadRequest(new { message = Localizer["ProvideAtLeastOneChange"].Value });
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(input.PdfContent))
                {
                    LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Template");
                    SafetyGuard.EnsureSafePdf(input.PdfContent, "Template");
                }
                var response = await PdfTemplateService.UpdateTemplateAsync(templateId, user.Id, input);
                return Ok(response);
            }
            catch (ApiValidationException ex) when (ex.Code == TemplateNotFoundCode)
            {
                return TemplateNotFound();
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Update template failed");
                return SafeProblem(Localizer["TemplateUpdateError"], ex);
            }
        }

        [HttpDelete("/api/pdf-template/{templateId:guid}")]
        public async Task<IActionResult> DeletePdfTemplate(Guid templateId)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                await PdfTemplateService.DeleteTemplateAsync(templateId, user.Id);
                return NoContent();
            }
            catch (ApiValidationException ex) when (ex.Code == TemplateNotFoundCode)
            {
                return TemplateNotFound();
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Delete template failed");
                return SafeProblem(Localizer["TemplateDeleteError"], ex);
            }
        }

        [CapacityPool(CapacityPoolKind.Ai)]
        [HttpPost("/api/ai/detect-fields")]
        [AllowCookieSession] // template builder (Views/Home/TemplateBuilder.cshtml)
        public async Task<IActionResult> DetectTemplateFields([FromBody] AiDetectFieldsInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (string.IsNullOrWhiteSpace(input.PdfContent))
            {
                return BadRequest(new { message = Localizer["PdfContentRequired"].Value });
            }

            // Bad input is refused before payment (400 before 402), like convert/fill/find-codes.
            byte[] pdf;
            try
            {
                pdf = GuardedPdfFromBase64(input.PdfContent, "AI detect");
                EnsurePageRange(input.PageFrom, input.PageTo);
            }
            catch (ApiValidationException ex)
            {
                return InputRefused(ex);
            }

            return await RunAiAsync(user, "ai-detect-fields", PricedOperations.AiDetectFields, input, "AiDetectionFailed",
                async ct => new PaidResult(await _templateAiService.DetectFieldsAsync(input, pdf, ct)));
        }

        [CapacityPool(CapacityPoolKind.Ai)]
        [HttpPost("/api/ai/extract-data")]
        public async Task<IActionResult> ExtractData([FromBody] AiExtractDataInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (string.IsNullOrWhiteSpace(input.PdfContent))
            {
                return BadRequest(new { message = Localizer["PdfContentRequired"].Value });
            }

            if (input.Columns == null || input.Columns.Count == 0)
            {
                return BadRequest(new { code = AiExtraction.InvalidColumnsCode, message = Localizer["ColumnsRequired"].Value });
            }

            // Columns, examples and the page range are checked before payment and before the model (#222).
            byte[] pdf;
            try
            {
                AiExtraction.Validate(input.Columns, input.Examples);
                EnsurePageRange(input.PageFrom, input.PageTo);
                pdf = GuardedPdfFromBase64(input.PdfContent, "AI extract-data");
            }
            catch (ApiValidationException ex)
            {
                if (ex.Code == AiExtraction.InvalidColumnsCode)
                {
                    Logger.LogInformation("AI extract-data refused: {Detail}", ex.Message);
                }
                return InputRefused(ex);
            }

            return await RunAiAsync(user, "ai-extract-data", PricedOperations.AiExtract, input, "AiExtractionFailed",
                async ct => new PaidResult(await _templateAiService.ExtractDataAsync(input, pdf, ct)));
        }

        private static void EnsurePageRange(int? from, int? to)
        {
            if (from is < 1 || to is < 1 || (from is { } f && to is { } t && t < f))
            {
                throw new ApiValidationException(AiDocument.InvalidPageRangeCode, "pageFrom and pageTo must be positive and pageFrom ≤ pageTo");
            }
        }

        /// <summary>
        /// The paid part of an AI call: the price (not offered without one), the reservation, the call, and
        /// its failures as codes — 403 AI_DISABLED, 503 AI_UNAVAILABLE, 422 AI_REFUSED, 502 AI_INVALID_OUTPUT /
        /// AI_PROVIDER_ERROR. A failed call releases the reservation.
        /// </summary>
        private async Task<IActionResult> RunAiAsync(User user, string operation, string priceKey, object input, string failedKey,
            Func<CancellationToken, Task<PaidResult>> work)
        {
            int cost;
            try
            {
                cost = Prices.Credits(priceKey);
            }
            catch (OperationNotOfferedException ex)
            {
                return OperationNotOffered(ex);
            }

            try
            {
                return await RunOperationAsync(user, operation, ApiOperationService.Fingerprint(operation, input), cost, work);
            }
            catch (AiException ex)
            {
                Logger.LogWarning("AI {Operation} failed: {Code} ({Detail})", operation, ex.Code, ex.Message);
                if (ex.StatusCode == StatusCodes.Status503ServiceUnavailable) Response.Headers.RetryAfter = "60";
                return StatusCode(ex.StatusCode, new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }
            catch (ApiValidationException ex)
            {
                return InputRefused(ex);
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "AI {Operation} failed", operation);
                return Problem(Localizer[failedKey].Value);
            }
        }
    }
}
