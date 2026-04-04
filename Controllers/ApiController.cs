using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ImageMagick;
using ZXing;
using ZXing.Common;
using Microsoft.Extensions.Options;
using iText.Kernel.Pdf;
using DotNetSigningServer.Options;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace DotNetSigningServer.Controllers
{
    [ApiController]
    [IgnoreAntiforgeryToken]
    public class ApiController : ControllerBase
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly PdfSigningService _signingService;
        private readonly PdfTemplateService _pdfTemplateService;
        private readonly IApiAuthService _apiAuthService;
        private readonly ILogger<ApiController> _logger;
        private readonly TemplateAiService _templateAiService;
        private readonly ContentLimitGuard _limitGuard;
        private readonly LimitsOptions _limitOptions;
        private readonly BillingOptions _billingOptions;
        private readonly PdfConversionService _pdfConversionService;
        private readonly FlowPipelineService _flowPipelineService;
        private readonly IWebHostEnvironment _env;
        private readonly IDataProtector _dataProtector;
        private const string AttachmentDebitBypassHeader = "X-P4PDF-Attachment-Billing-Bypass";

        public ApiController(ApplicationDbContext dbContext, PdfSigningService signingService, PdfTemplateService pdfTemplateService, IApiAuthService apiAuthService, ILogger<ApiController> logger, TemplateAiService templateAiService, ContentLimitGuard limitGuard, IOptions<LimitsOptions> limitOptions, IOptions<BillingOptions> billingOptions, PdfConversionService pdfConversionService, FlowPipelineService flowPipelineService, IWebHostEnvironment env, IDataProtectionProvider dataProtectionProvider)
        {
            _dbContext = dbContext;
            _signingService = signingService;
            _pdfTemplateService = pdfTemplateService;
            _apiAuthService = apiAuthService;
            _logger = logger;
            _templateAiService = templateAiService;
            _limitGuard = limitGuard;
            _limitOptions = limitOptions.Value;
            _billingOptions = billingOptions.Value;
            _pdfConversionService = pdfConversionService;
            _flowPipelineService = flowPipelineService;
            _env = env;
            _dataProtector = dataProtectionProvider.CreateProtector("SigningData.TsaCredentials");
        }

        /// <summary>
        /// Returns a Problem response with trace ID. Only includes exception details in Development.
        /// </summary>
        private ObjectResult SafeProblem(string genericMessage, Exception? ex = null)
        {
            var traceId = HttpContext.TraceIdentifier;
            var detail = _env.IsDevelopment() && ex != null
                ? $"{genericMessage}: {ex.Message}"
                : $"{genericMessage} (traceId: {traceId})";

            return Problem(detail: detail, statusCode: StatusCodes.Status500InternalServerError);
        }

        [HttpPost("/api/presign")]
        public async Task<IActionResult> PreSign([FromBody] PreSignInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                _limitGuard.EnsurePdfWithinLimit(input.PdfContent, "Presign");
                _limitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            try
            {
                var signingData = new SigningData();
                if (input.TemplateId.HasValue)
                {
                    var signatureField = await GetSignatureFieldAsync(input.TemplateId.Value, user.Id);
                    input.SignRect = signatureField.Rect;
                    input.SignPageNumber = signatureField.Page <= 0 ? 1 : signatureField.Page;
                    input.FieldName = string.IsNullOrWhiteSpace(signatureField.FieldName)
                        ? $"Signature_{signingData.Id.Replace("-", string.Empty)}"
                        : signatureField.FieldName;
                }

                string fieldName = string.IsNullOrWhiteSpace(input.FieldName)
                    ? $"Signature_{signingData.Id.Replace("-", string.Empty)}"
                    : input.FieldName;

                var (presignedPdfPath, hashToSign) = _signingService.HandlePreSign(input, fieldName);
                signingData.FieldName = fieldName;
                signingData.PresignedPdfPath = presignedPdfPath;
                signingData.HashToSign = hashToSign;
                signingData.CertificatePem = input.CertificatePem;
                signingData.TsaUrl = input.TsaUrl;
                signingData.TsaUsername = !string.IsNullOrEmpty(input.TsaUsername)
                    ? _dataProtector.Protect(input.TsaUsername) : null;
                signingData.TsaPassword = !string.IsNullOrEmpty(input.TsaPassword)
                    ? _dataProtector.Protect(input.TsaPassword) : null;

                signingData.UserId = user.Id;

                _dbContext.SigningData.Add(signingData);
                await _dbContext.SaveChangesAsync();

                return Ok(new { id = signingData.Id, hashToSign = signingData.HashToSign });
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "Presign failed");
                return SafeProblem("An error occurred during the presign process", ex);
            }
        }

        [HttpPost("/api/pdf-template")]
        public async Task<IActionResult> CreatePdfTemplate([FromBody] CreateTemplateInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (input.Fields == null || input.Fields.Count == 0)
            {
                return BadRequest(new { message = "At least one field definition is required." });
            }
            if (string.IsNullOrWhiteSpace(input.PdfContent))
            {
                return BadRequest(new { message = "PdfContent is required." });
            }

            try
            {
                var response = await _pdfTemplateService.CreateTemplateAsync(input, user.Id);
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "Create template failed");
                return SafeProblem("An error occurred while creating the template", ex);
            }
        }

        [HttpGet("/api/pdf-template")]
        public async Task<IActionResult> ListPdfTemplates()
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            var templates = await _pdfTemplateService.ListTemplatesAsync(user.Id);
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
                var template = await _pdfTemplateService.GetTemplateAsync(templateId, user.Id);
                return Ok(template);
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "Get template failed");
                return SafeProblem("An error occurred while retrieving the template", ex);
            }
        }

        [HttpPost("/api/ai/detect-fields")]
        public async Task<IActionResult> DetectTemplateFields([FromBody] AiDetectFieldsInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (!_templateAiService.IsEnabled)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "AI detection is not configured." });
            }

            if (string.IsNullOrWhiteSpace(input.PdfContent))
            {
                return BadRequest(new { message = "PdfContent is required." });
            }

            try
            {
                _limitGuard.EnsurePdfWithinLimit(input.PdfContent, "AI detect");
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
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "AI detect fields failed");
                return Problem("AI detection failed. Check AI configuration and try again.");
            }
        }

        [HttpPut("/api/pdf-template/{templateId:guid}")]
        public async Task<IActionResult> UpdatePdfTemplate(Guid templateId, [FromBody] UpdateTemplateInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (input.Fields != null && input.Fields.Count == 0 && string.IsNullOrWhiteSpace(input.PdfContent) && input.TemplateName == null)
            {
                return BadRequest(new { message = "Provide at least one change (fields, pdfContent, or templateName)." });
            }

            try
            {
                var response = await _pdfTemplateService.UpdateTemplateAsync(templateId, user.Id, input);
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "Update template failed");
                return SafeProblem("An error occurred while updating the template", ex);
            }
        }

        [HttpDelete("/api/pdf-template/{templateId:guid}")]
        public async Task<IActionResult> DeletePdfTemplate(Guid templateId)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                await _pdfTemplateService.DeleteTemplateAsync(templateId, user.Id);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "Delete template failed");
                return SafeProblem("An error occurred while deleting the template", ex);
            }
        }

        [HttpPost("/api/sign")]
        public async Task<IActionResult> Sign([FromBody] SignInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            var signingData = await _dbContext.SigningData.FindAsync(input.Id);
            if (signingData == null)
            {
                return NotFound(new { message = "Signing data not found for the provided ID." });
            }
            if (signingData.UserId != user.Id)
            {
                return Forbid();
            }

            try
            {
                var tsaUsername = !string.IsNullOrEmpty(signingData.TsaUsername)
                    ? _dataProtector.Unprotect(signingData.TsaUsername) : null;
                var tsaPassword = !string.IsNullOrEmpty(signingData.TsaPassword)
                    ? _dataProtector.Unprotect(signingData.TsaPassword) : null;

                var result = _signingService.HandleSign(
                    input,
                    signingData.PresignedPdfPath,
                    signingData.CertificatePem,
                    signingData.FieldName,
                    signingData.TsaUrl,
                    tsaUsername,
                    tsaPassword);

                System.IO.File.Delete(signingData.PresignedPdfPath);
                _dbContext.SigningData.Remove(signingData);
                await DebitUserAsync(user);

                return Ok(new { result });
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "Sign failed");
                return SafeProblem("An error occurred during the final signing process", ex);
            }
        }

        [HttpPost("/api/sign-pfx")]
        public async Task<IActionResult> SignWithPfx([FromBody] PfxSignInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                _limitGuard.EnsurePdfWithinLimit(input.PdfContent, "Sign with PFX");
                _limitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            try
            {
                if (input.TemplateId.HasValue)
                {
                    var signatureField = await GetSignatureFieldAsync(input.TemplateId.Value, user.Id);
                    input.SignRect = signatureField.Rect;
                    input.SignPageNumber = signatureField.Page <= 0 ? 1 : signatureField.Page;
                    input.FieldName = string.IsNullOrWhiteSpace(signatureField.FieldName)
                        ? input.FieldName
                        : signatureField.FieldName;
                }
                var result = _signingService.SignWithPfx(input);
                await DebitUserAsync(user);
                return Ok(new { result });
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "PFX sign failed");
                return SafeProblem("An error occurred during the PFX signing process", ex);
            }
        }

        [HttpPost("/api/timestamp")]
        public async Task<IActionResult> ApplyTimestamp([FromBody] DocumentTimestampInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                _limitGuard.EnsurePdfWithinLimit(input.PdfContent, "Timestamp");
                _limitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            try
            {
                if (input.TemplateId.HasValue)
                {
                    var signatureField = await GetSignatureFieldAsync(input.TemplateId.Value, user.Id);
                    input.SignRect = signatureField.Rect;
                    input.SignPageNumber = signatureField.Page <= 0 ? 1 : signatureField.Page;
                    input.FieldName = string.IsNullOrWhiteSpace(signatureField.FieldName)
                        ? input.FieldName
                        : signatureField.FieldName;
                }
                var result = _signingService.ApplyDocumentTimestamp(input);
                await DebitUserAsync(user);
                return Ok(new { result });
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "Timestamp failed");
                return SafeProblem("An error occurred while applying the timestamp", ex);
            }
        }

        [HttpPost("/api/visual-sign")]
        public async Task<IActionResult> ApplyVisualSign([FromBody] VisualSignInput input)
        {
            _logger.LogInformation("[visual-sign] Received request: PdfContent length={PdfLen}, SignImage length={ImgLen}, Rect=({X},{Y},{W},{H}), Page={Page}, TemplateId={TemplateId}, HasAppearance={HasAppearance}",
                input.PdfContent?.Length ?? 0,
                input.SignImageContent?.Length ?? 0,
                input.SignRect?.X, input.SignRect?.Y, input.SignRect?.Width, input.SignRect?.Height,
                input.SignPageNumber,
                input.TemplateId,
                input.Appearance != null);

            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null)
            {
                _logger.LogWarning("[visual-sign] Auth/credits check failed, user={User}, error type={ErrorType}", user?.Email, error?.GetType().Name);
                return error!;
            }
            _logger.LogInformation("[visual-sign] Authenticated as {User}", user.Email);

            try
            {
                _limitGuard.EnsurePdfWithinLimit(input.PdfContent, "Visual sign");
                _limitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning("[visual-sign] Limit check failed: {Message}", ex.Message);
                return BadRequest(new { message = ex.Message });
            }

            try
            {
                if (input.TemplateId.HasValue)
                {
                    _logger.LogInformation("[visual-sign] Resolving template {TemplateId}", input.TemplateId.Value);
                    var signatureField = await GetSignatureFieldAsync(input.TemplateId.Value, user.Id);
                    input.SignRect = signatureField.Rect;
                    input.SignPageNumber = signatureField.Page <= 0 ? 1 : signatureField.Page;
                    _logger.LogInformation("[visual-sign] Template resolved, Rect=({X},{Y},{W},{H}), Page={Page}",
                        input.SignRect?.X, input.SignRect?.Y, input.SignRect?.Width, input.SignRect?.Height, input.SignPageNumber);
                }
                _logger.LogInformation("[visual-sign] Applying visual signature...");
                var result = _signingService.ApplyVisualSign(input);
                _logger.LogInformation("[visual-sign] Success, result length={Len}", result?.Length ?? 0);
                await DebitUserAsync(user);
                return Ok(new { result });
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "[visual-sign] Failed: {Message}", ex.Message);
                return SafeProblem("An error occurred while applying the visual signature", ex);
            }
        }

        [HttpPost("/api/seal")]
        public async Task<IActionResult> ApplySeal([FromBody] SealInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                _limitGuard.EnsurePdfWithinLimit(input.PdfContent, "Seal");
                _limitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature");
                _limitGuard.EnsureImageWithinLimit(input.StampImageContent, "Stamp");
                _limitGuard.EnsureImageWithinLimit(input.CompanyLogoContent, "Company logo");
                _limitGuard.EnsureImageWithinLimit(input.BackgroundImageContent, "Background");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            try
            {
                if (input.TemplateId.HasValue)
                {
                    var signatureField = await GetSignatureFieldAsync(input.TemplateId.Value, user.Id);
                    input.SignRect = signatureField.Rect;
                    input.SignPageNumber = signatureField.Page <= 0 ? 1 : signatureField.Page;
                }

                var result = _signingService.ApplySeal(input);
                await DebitUserAsync(user);
                return Ok(new { result });
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "Seal failed");
                return SafeProblem("An error occurred while applying the seal", ex);
            }
        }

        [HttpPost("/api/attachment")]
        public async Task<IActionResult> AddAttachment([FromBody] AddAttachmentInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            var bypassDebitRequested = Request.Headers.ContainsKey(AttachmentDebitBypassHeader);
            if (bypassDebitRequested && !IsAttachmentDebitBypassAuthorized())
            {
                _logger.LogWarning("Attachment billing bypass rejected for user {UserId}", user.Id);
                return Forbid();
            }

            try
            {
                _limitGuard.EnsurePdfWithinLimit(input.PdfContent, "Attachment PDF");
                _limitGuard.EnsureAttachmentWithinLimit(input.AttachmentContent, "Attachment");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            try
            {
                var result = _signingService.AddAttachment(input);
                if (!bypassDebitRequested)
                {
                    await DebitUserAsync(user);
                }
                return Ok(new { result });
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "Add attachment failed");
                return SafeProblem("An error occurred while adding the attachment", ex);
            }
        }

        [HttpPost("/api/flow")]
        public async Task<IActionResult> StartFlow([FromBody] FlowPipelineInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if ((input.PdfContents == null || input.PdfContents.Count == 0) && input.FillPdf == null)
            {
                return BadRequest(new { message = "Provide PdfContents or FillPdf." });
            }

            if (input.Flow == null || input.Flow.Count == 0)
            {
                return BadRequest(new { message = "Flow is required." });
            }

            var invalid = input.Flow.FirstOrDefault(f =>
                string.IsNullOrWhiteSpace(f.Action) ||
                !IsAllowedFlowAction(f.Action));
            if (invalid != null)
            {
                return BadRequest(new { message = $"Unsupported flow action '{invalid.Action}'." });
            }

            var terminalActions = input.Flow.Count(f => IsTerminalAction(f.Action));
            if (terminalActions > 1)
            {
                return BadRequest(new { message = "Only one of presign, timestamp, sign-pfx is allowed in the flow." });
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
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "Flow start failed");
                return SafeProblem("An error occurred while starting the flow", ex);
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
                return BadRequest(new { message = "Signatures are required." });
            }

            try
            {
                var status = await _flowPipelineService.CompleteSignaturesAsync(request.FlowId, user.Id, request.Signatures, HttpContext.RequestAborted);
                return Ok(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "Flow sign failed");
                return SafeProblem("An error occurred while completing the flow signatures", ex);
            }
        }

        [HttpPost("/api/convert/pdfa")]
        public async Task<IActionResult> ConvertToPdfA([FromBody] ConvertToPdfAInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (string.IsNullOrWhiteSpace(input.PdfContent))
            {
                return BadRequest(new { message = "PdfContent is required." });
            }

            try
            {
                _limitGuard.EnsurePdfWithinLimit(input.PdfContent, "PDF/A conversion");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            try
            {
                var pageCount = CountPagesFromBase64(input.PdfContent);
                var requiredCredits = 1;
                if (requiredCredits > 0 && user.CreditsRemaining < requiredCredits)
                {
                    return PaymentRequired(user, requiredCredits);
                }

                var result = _pdfConversionService.ConvertToPdfA(input);
                if (requiredCredits > 0)
                {
                    await DebitUserAsync(user, requiredCredits);
                }

                var conformance = PdfConversionService.FormatConformance(input.Conformance);
                return Ok(new { result, conformance });
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "PDF/A conversion failed");
                return SafeProblem("An error occurred while converting the document to PDF/A", ex);
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
                return BadRequest(new { message = "Provide either TemplateId or PdfContent (but not both)." });
            }
            if (!hasTemplate && (input.Fields == null || input.Fields.Count == 0))
            {
                return BadRequest(new { message = "Fields are required when using PdfContent directly." });
            }
            if (input.Data == null || input.Data.Count == 0)
            {
                return BadRequest(new { message = "At least one data set is required." });
            }

            try
            {
                if (hasContent)
                {
                    _limitGuard.EnsurePdfWithinLimit(input.PdfContent, "Fill PDF");
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

                var response = await _pdfTemplateService.FillAsync(input, user.Id);
                if (requiredCredits > 0)
                {
                    await DebitUserAsync(user, requiredCredits);
                }
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "Fill PDF failed");
                return SafeProblem("An error occurred while filling the PDF", ex);
            }
        }

        [HttpPost("/api/find-codes")]
        public async Task<IActionResult> FindCodes([FromBody] FindCodesInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (string.IsNullOrWhiteSpace(input.PdfContent))
            {
                return BadRequest(new { message = "PdfContent is required." });
            }

            try
            {
                _limitGuard.EnsurePdfWithinLimit(input.PdfContent, "Barcode scan");
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
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "Barcode scan failed");
                return SafeProblem("An error occurred while scanning codes", ex);
            }
        }

        private IList<BarcodeFormat> ParseFormats(string codeType)
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
                throw new InvalidOperationException("PdfContent is not valid base64.");
            }

            var results = new List<object>();
            var seen = new HashSet<string>(); // pro deduplikaci

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

                // základ – sRGB, bez alfy
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
                                continue; // už jsme ho přidali z jiné varianty

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
                    // uklidit všechny varianty
                    foreach (var v in variants)
                        v.Dispose();

                    baseVariant.Dispose();
                }
            }

            return results;
        }

        /// <summary>
        /// Vytvoří různé varianty obrázku, které zvyšují šanci na detekci.
        /// </summary>
        private static List<IMagickImage> CreateVariants(IMagickImage baseVariant)
        {
            var variants = new List<IMagickImage>();

            // 1) originál (už je v sRGB, bez alfy)
            variants.Add(((MagickImage)baseVariant).Clone());

            // 2) grayscale + zvýšení kontrastu
            var gray = ((MagickImage)baseVariant).Clone();
            gray.ColorType = ColorType.Grayscale;
            gray.Contrast();
            variants.Add(gray);

            // 3) grayscale + adaptive sharpen
            var sharpen = gray.Clone();
            sharpen.AdaptiveSharpen();
            variants.Add(sharpen);

            // 4) zvětšení (pro malé kódy) – z gray verze
            if (baseVariant.Width < 2000 || baseVariant.Height < 2000)
            {
                var scaled1 = gray.Clone();
                scaled1.Resize((uint)(baseVariant.Width * 1.5), (uint)(baseVariant.Height * 1.5));
                variants.Add(scaled1);

                var scaled2 = gray.Clone();
                scaled2.Resize((uint)(baseVariant.Width * 2.0), (uint)(baseVariant.Height * 2.0));
                variants.Add(scaled2);
            }

            // // 5) rotace – některé 1D kódy jsou prostě „naležato“
            // var rotated90 = gray.Clone();
            // rotated90.Rotate(90);
            // variants.Add(rotated90);

            // var rotated270 = gray.Clone();
            // rotated270.Rotate(270);
            // variants.Add(rotated270);

            // 6) jednoduchý threshold – někdy pomůže úplné zčernobílení
            var threshold = gray.Clone();
            threshold.Threshold(new Percentage(60)); // můžeš si pohrát s hodnotou
            variants.Add(threshold);

            return variants;
        }

        /// <summary>
        /// Zkusí dekódovat jeden variant obrázku.
        /// </summary>
        private static List<Result> TryDecodeVariant(IMagickImage variant, BarcodeReaderGeneric reader)
        {
            var rgba = variant.ToByteArray(MagickFormat.Rgba);

            var luminance = new RGBLuminanceSource(
                rgba,
                (int)variant.Width,
                (int)variant.Height,
                RGBLuminanceSource.BitmapFormat.RGBA32
            );

            // Primárně zkusíme DecodeMultiple
            var decodedMultiple = reader.DecodeMultiple(luminance);
            if (decodedMultiple is { Length: > 0 })
            {
                return decodedMultiple.ToList();
            }

            // fallback na single decode
            var single = reader.Decode(luminance);
            return single != null ? new List<Result> { single } : new List<Result>();
        }
        private async Task<User?> GetAuthenticatedUserAsync(string? originHeader = null)
        {
            // Prefer cookie-authenticated user
            if (User?.Identity?.IsAuthenticated == true)
            {
                var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                                  ?? User.FindFirst("sub")?.Value;
                if (Guid.TryParse(userIdClaim, out var guid))
                {
                    var cookieUser = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == guid);
                    if (cookieUser != null) return cookieUser;
                }
            }

            // Fallback to API token
            _logger.LogDebug("Validating API token from Authorization header");
            var tokenUser = await _apiAuthService.ValidateTokenAsync(Request.Headers["Authorization"].ToString(), originHeader, HttpContext.Connection.RemoteIpAddress);
            if (tokenUser == null)
            {
                return null;
            }

            return await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == tokenUser.Id);
        }

        private bool IsAttachmentDebitBypassAuthorized()
        {
            var configuredKey = _billingOptions.AttachmentDebitBypassKey?.Trim();
            if (string.IsNullOrWhiteSpace(configuredKey))
            {
                return false;
            }

            var providedKey = Request.Headers[AttachmentDebitBypassHeader].ToString().Trim();
            if (string.IsNullOrWhiteSpace(providedKey))
            {
                return false;
            }

            return FixedTimeEquals(providedKey, configuredKey);
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("comparison-key"));
            var leftHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(left));
            var rightHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(right));
            return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
        }

        private async Task<(User? user, ActionResult? error)> EnsureUserWithCreditsAsync(int requiredCredits = 1, string? originHeader = null)
        {
            var user = await GetAuthenticatedUserAsync(originHeader);
            if (user == null)
            {
                _logger.LogWarning(Logging.LoggingEvents.AuthFailed, "API auth failed");
                return (null, Unauthorized());
            }

            if (requiredCredits > 0 && user.CreditsRemaining < requiredCredits)
            {
                _logger.LogWarning(Logging.LoggingEvents.CreditsInsufficient, "Credits insufficient for user {UserId}", user.Id);
                return (null, StatusCode(StatusCodes.Status402PaymentRequired, new { message = "No credits remaining. Please purchase more to continue." }));
            }

            return (user, null);
        }

        private async Task<bool> DebitUserAsync(User user, int debit = 1)
        {
            if (debit <= 0)
            {
                return true;
            }

            var rowsAffected = await _dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE \"Users\" SET \"CreditsRemaining\" = \"CreditsRemaining\" - {0} WHERE \"Id\" = {1} AND \"CreditsRemaining\" >= {2}",
                debit, user.Id, debit);

            if (rowsAffected == 0)
            {
                return false;
            }

            // Refresh the in-memory entity to reflect the new value
            await _dbContext.Entry(user).ReloadAsync();
            return true;
        }

        private async Task<PdfFieldDefinition> GetSignatureFieldAsync(Guid templateId, Guid userId)
        {
            var template = await _pdfTemplateService.GetTemplateAsync(templateId, userId);
            var signatureField = template.Fields.FirstOrDefault(f => f.Type == PdfFieldType.Signature);
            if (signatureField == null)
            {
                throw new InvalidOperationException("Template does not contain a signature field.");
            }
            return signatureField;
        }

        private IActionResult PaymentRequired(User user, int requiredCredits)
        {
            _logger.LogWarning(Logging.LoggingEvents.CreditsInsufficient, "Credits insufficient for user {UserId}", user.Id);
            return StatusCode(StatusCodes.Status402PaymentRequired, new { message = $"Not enough credits. {requiredCredits} credit(s) required." });
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
                    var template = await _pdfTemplateService.GetTemplateAsync(input.FillPdf.TemplateId.Value, userId);
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

        private static int CalculateCreditsForPages(int pageCount)
        {
            if (pageCount <= 0) return 0;
            return (int)Math.Ceiling(pageCount / 3.0);
        }

        private static int CountPagesFromBase64(string pdfBase64)
        {
            using var ms = new MemoryStream(Convert.FromBase64String(pdfBase64));
            using var reader = new PdfReader(ms);
            using var pdf = new PdfDocument(reader);
            return pdf.GetNumberOfPages();
        }

        private async Task<(string pdfBase64, int pageCount)> ResolvePdfForFillAsync(FillPdfInput input, Guid userId)
        {
            if (input.TemplateId != null)
            {
                var template = await _pdfTemplateService.GetTemplateAsync(input.TemplateId.Value, userId);
                var pages = CountPagesFromBase64(template.PdfContent);
                return (template.PdfContent, pages);
            }

            var count = CountPagesFromBase64(input.PdfContent);
            return (input.PdfContent, count);
        }
    }
}
