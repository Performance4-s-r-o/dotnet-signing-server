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

        public ApiController(ApplicationDbContext dbContext, PdfSigningService signingService, PdfTemplateService pdfTemplateService, IApiAuthService apiAuthService, ILogger<ApiController> logger, TemplateAiService templateAiService, ContentLimitGuard limitGuard, IOptions<LimitsOptions> limitOptions)
        {
            _dbContext = dbContext;
            _signingService = signingService;
            _pdfTemplateService = pdfTemplateService;
            _apiAuthService = apiAuthService;
            _logger = logger;
            _templateAiService = templateAiService;
            _limitGuard = limitGuard;
            _limitOptions = limitOptions.Value;
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
                signingData.TsaUsername = input.TsaUsername;
                signingData.TsaPassword = input.TsaPassword;

                signingData.UserId = user.Id;

                _dbContext.SigningData.Add(signingData);
                await _dbContext.SaveChangesAsync();

                return Ok(new { id = signingData.Id, hashToSign = signingData.HashToSign });
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "Presign failed");
                return Problem($"An error occurred during the presign process: {ex.Message}");
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
                return Problem($"An error occurred while creating the template: {ex.Message}");
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
        public async Task<IActionResult> GetPdfTemplate(Guid templateId)
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
                return Problem($"An error occurred while retrieving the template: {ex.Message}");
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
                return Problem($"An error occurred while updating the template: {ex.Message}");
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
                return Problem($"An error occurred while deleting the template: {ex.Message}");
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
                var result = _signingService.HandleSign(
                    input,
                    signingData.PresignedPdfPath,
                    signingData.CertificatePem,
                    signingData.FieldName,
                    signingData.TsaUrl,
                    signingData.TsaUsername,
                    signingData.TsaPassword);

                System.IO.File.Delete(signingData.PresignedPdfPath);
                _dbContext.SigningData.Remove(signingData);
                await DebitUserAsync(user);

                return Ok(new { result });
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "Sign failed");
                return Problem($"An error occurred during the final signing process: {ex.Message}");
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
                return Problem($"An error occurred during the PFX signing process: {ex.Message}");
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
                return Problem($"An error occurred while applying the timestamp: {ex.Message}");
            }
        }

        [HttpPost("/api/attachment")]
        public async Task<IActionResult> AddAttachment([FromBody] AddAttachmentInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

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
                await DebitUserAsync(user);
                return Ok(new { result });
            }
            catch (Exception ex)
            {
                _logger.LogError(Logging.LoggingEvents.ApiError, ex, "Add attachment failed");
                return Problem($"An error occurred while adding the attachment: {ex.Message}");
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
                return Problem($"An error occurred while filling the PDF: {ex.Message}");
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
                return Problem($"An error occurred while scanning codes: {ex.Message}");
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

        private async Task<List<object>> DetectCodesAsync(string base64Pdf, IList<BarcodeFormat> formats)
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

            var reader = new BarcodeReaderGeneric
            {
                Options = new DecodingOptions
                {
                    PossibleFormats = formats.ToList(),
                    TryHarder = true,
                    TryInverted = true,
                    PureBarcode = false
                },
                AutoRotate = true
            };

            for (var pageIndex = 0; pageIndex < collection.Count; pageIndex++)
            {
                var page = collection[pageIndex];
                var rgba = page.ToByteArray(MagickFormat.Rgba);
                var luminance = new RGBLuminanceSource(rgba, (int)page.Width, (int)page.Height, RGBLuminanceSource.BitmapFormat.RGBA32);
                var decoded = reader.DecodeMultiple(luminance);
                if (decoded == null) continue;

                foreach (var code in decoded)
                {
                    var point = code.ResultPoints?.FirstOrDefault();
                    results.Add(new
                    {
                        value = code.Text,
                        codeType = code.BarcodeFormat.ToString(),
                        position = new { x = point?.X ?? 0, y = point?.Y ?? 0 },
                        page = pageIndex + 1
                    });
                }
            }

            return results;
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
            var tokenUser = await _apiAuthService.ValidateTokenAsync(Request.Headers["Authorization"].ToString(), originHeader);
            if (tokenUser == null)
            {
                return null;
            }

            return await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == tokenUser.Id);
        }

        private async Task<(User? user, IActionResult? error)> EnsureUserWithCreditsAsync(int requiredCredits = 1, string? originHeader = null)
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

        private async Task DebitUserAsync(User user, int debit = 1)
        {
            if (debit <= 0)
            {
                return;
            }

            user.CreditsRemaining = Math.Max(0, user.CreditsRemaining - debit);
            _dbContext.Users.Update(user);
            await _dbContext.SaveChangesAsync();
        }

        private async Task<PdfFieldDefinition> GetSignatureFieldAsync(Guid templateId, Guid userId)
        {
            var template = await _pdfTemplateService.GetTemplateAsync(templateId, userId);
            var signatureField = template.Fields.FirstOrDefault(f => string.Equals(f.Type, "signature", StringComparison.OrdinalIgnoreCase));
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
