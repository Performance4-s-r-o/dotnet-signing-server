using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
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
                var response = await PdfTemplateService.CreateTemplateAsync(input, user.Id);
                return Ok(response);
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
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Get template failed");
                return SafeProblem(Localizer["TemplateRetrieveError"], ex);
            }
        }

        [HttpPut("/api/pdf-template/{templateId:guid}")]
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
                var response = await PdfTemplateService.UpdateTemplateAsync(templateId, user.Id, input);
                return Ok(response);
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
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Delete template failed");
                return SafeProblem(Localizer["TemplateDeleteError"], ex);
            }
        }

        [HttpPost("/api/ai/detect-fields")]
        public async Task<IActionResult> DetectTemplateFields([FromBody] AiDetectFieldsInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (!_templateAiService.IsEnabled)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = Localizer["AiNotConfigured"].Value });
            }

            if (string.IsNullOrWhiteSpace(input.PdfContent))
            {
                return BadRequest(new { message = Localizer["PdfContentRequired"].Value });
            }

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "AI detect");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            try
            {
                var fields = await _templateAiService.DetectFieldsAsync(input.PdfContent, input.Prompt, HttpContext.RequestAborted);
                return Ok(new AiDetectFieldsResponse { Fields = fields.ToList() });
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "AI detect fields failed");
                return Problem(Localizer["AiDetectionFailed"].Value);
            }
        }

        [HttpPost("/api/ai/extract-data")]
        public async Task<IActionResult> ExtractData([FromBody] AiExtractDataInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (!_templateAiService.IsEnabled)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = Localizer["AiExtractionNotConfigured"].Value });
            }

            if (string.IsNullOrWhiteSpace(input.PdfContent))
            {
                return BadRequest(new { message = Localizer["PdfContentRequired"].Value });
            }

            if (input.Columns == null || input.Columns.Count == 0)
            {
                return BadRequest(new { message = Localizer["ColumnsRequired"].Value });
            }

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "AI extract-data");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            try
            {
                var values = await _templateAiService.ExtractDataAsync(input.PdfContent, input.Columns, HttpContext.RequestAborted);
                return Ok(new AiExtractDataResponse { Values = values.ToList() });
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "AI data extraction failed");
                return Problem(Localizer["AiExtractionFailed"].Value);
            }
        }
    }
}
