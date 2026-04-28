using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Controllers
{
    [Route("api")]
    public class PdfSigningApiController : ApiControllerBase
    {
        private readonly PdfSigningService _signingService;
        private readonly PdfSealingService _sealingService;
        private readonly IDataProtector _dataProtector;
        private const string AttachmentDebitBypassHeader = "X-P4PDF-Attachment-Billing-Bypass";

        public PdfSigningApiController(
            ApplicationDbContext dbContext,
            PdfSigningService signingService,
            PdfSealingService sealingService,
            IApiAuthService apiAuthService,
            ILogger<PdfSigningApiController> logger,
            ContentLimitGuard limitGuard,
            IOptions<BillingOptions> billingOptions,
            IWebHostEnvironment env,
            PdfTemplateService pdfTemplateService,
            IDataProtectionProvider dataProtectionProvider,
            IStringLocalizer<SharedStrings> localizer)
            : base(dbContext, apiAuthService, logger, limitGuard, billingOptions, env, pdfTemplateService, localizer)
        {
            _signingService = signingService;
            _sealingService = sealingService;
            _dataProtector = dataProtectionProvider.CreateProtector("SigningData.TsaCredentials");
        }

        [HttpPost("/api/presign")]
        public async Task<IActionResult> PreSign([FromBody] PreSignInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Presign");
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
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

                DbContext.SigningData.Add(signingData);
                await DbContext.SaveChangesAsync();

                return Ok(new { id = signingData.Id, hashToSign = signingData.HashToSign });
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Presign failed");
                return SafeProblem(Localizer["PresignError"], ex);
            }
        }

        [HttpPost("/api/sign")]
        public async Task<IActionResult> Sign([FromBody] SignInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            var signingData = await DbContext.SigningData.FindAsync(input.Id);
            if (signingData == null)
            {
                return NotFound(new { message = Localizer["SigningDataNotFound"].Value });
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
                DbContext.SigningData.Remove(signingData);
                await DebitUserAsync(user);

                return PdfOrJsonResult(result);
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Sign failed");
                return SafeProblem(Localizer["SignError"], ex);
            }
        }

        [HttpPost("/api/sign-pfx")]
        public async Task<IActionResult> SignWithPfx([FromBody] PfxSignInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Sign with PFX");
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
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
                return PdfOrJsonResult(result);
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "PFX sign failed");
                return SafeProblem(Localizer["PfxSignError"], ex);
            }
        }

        [HttpPost("/api/timestamp")]
        public async Task<IActionResult> ApplyTimestamp([FromBody] DocumentTimestampInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Timestamp");
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
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
                return PdfOrJsonResult(result);
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Timestamp failed");
                return SafeProblem(Localizer["TimestampError"], ex);
            }
        }

        [HttpPost("/api/visual-sign")]
        public async Task<IActionResult> ApplyVisualSign([FromBody] VisualSignInput input)
        {
            Logger.LogInformation("[visual-sign] Received request: PdfContent length={PdfLen}, SignImage length={ImgLen}, Rect=({X},{Y},{W},{H}), Page={Page}, TemplateId={TemplateId}, HasAppearance={HasAppearance}",
                input.PdfContent?.Length ?? 0,
                input.SignImageContent?.Length ?? 0,
                input.SignRect?.X, input.SignRect?.Y, input.SignRect?.Width, input.SignRect?.Height,
                input.SignPageNumber,
                input.TemplateId,
                input.Appearance != null);

            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null)
            {
                Logger.LogWarning("[visual-sign] Auth/credits check failed, user={UserId}, error type={ErrorType}", user?.Id, error?.GetType().Name);
                return error!;
            }
            Logger.LogInformation("[visual-sign] Authenticated as {UserId}", user.Id);

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Visual sign");
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
            }
            catch (InvalidOperationException ex)
            {
                Logger.LogWarning("[visual-sign] Limit check failed: {Message}", ex.Message);
                return BadRequest(new { message = ex.Message });
            }

            try
            {
                if (input.TemplateId.HasValue)
                {
                    Logger.LogInformation("[visual-sign] Resolving template {TemplateId}", input.TemplateId.Value);
                    var signatureField = await GetSignatureFieldAsync(input.TemplateId.Value, user.Id);
                    input.SignRect = signatureField.Rect;
                    input.SignPageNumber = signatureField.Page <= 0 ? 1 : signatureField.Page;
                    Logger.LogInformation("[visual-sign] Template resolved, Rect=({X},{Y},{W},{H}), Page={Page}",
                        input.SignRect?.X, input.SignRect?.Y, input.SignRect?.Width, input.SignRect?.Height, input.SignPageNumber);
                }
                Logger.LogInformation("[visual-sign] Applying visual signature...");
                var result = _signingService.ApplyVisualSign(input);
                Logger.LogInformation("[visual-sign] Success, result length={Len}", result?.Length ?? 0);
                await DebitUserAsync(user);
                return PdfOrJsonResult(result);
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "[visual-sign] Failed: {Message}", ex.Message);
                return SafeProblem(Localizer["VisualSignError"], ex);
            }
        }

        [HttpPost("/api/seal")]
        public async Task<IActionResult> ApplySeal([FromBody] SealInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Seal");
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature");
                LimitGuard.EnsureImageWithinLimit(input.StampImageContent, "Stamp");
                LimitGuard.EnsureImageWithinLimit(input.CompanyLogoContent, "Company logo");
                LimitGuard.EnsureImageWithinLimit(input.BackgroundImageContent, "Background");
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

                var result = _sealingService.ApplySeal(input);
                await DebitUserAsync(user);
                return PdfOrJsonResult(result);
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Seal failed");
                return SafeProblem(Localizer["SealError"], ex);
            }
        }

        [HttpPost("/api/attachment")]
        public async Task<IActionResult> AddAttachment([FromBody] AddAttachmentInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            var bypassDebitRequested = Request.Headers.ContainsKey(AttachmentDebitBypassHeader);
            Logger.LogInformation(
                "[attachment] user={UserId} bypassHeaderPresent={HeaderPresent} providedFp={ProvidedFp} configuredFp={ConfiguredFp}",
                user.Id,
                bypassDebitRequested,
                Fingerprint(Request.Headers[AttachmentDebitBypassHeader].ToString()),
                Fingerprint(BillingOptions.AttachmentDebitBypassKey));
            if (bypassDebitRequested && !IsAttachmentDebitBypassAuthorized())
            {
                Logger.LogWarning("Attachment billing bypass rejected for user {UserId}", user.Id);
                return Forbid();
            }

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Attachment PDF");
                LimitGuard.EnsureAttachmentWithinLimit(input.AttachmentContent, "Attachment");
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
                return PdfOrJsonResult(result);
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Add attachment failed");
                return SafeProblem(Localizer["AttachmentError"], ex);
            }
        }

        private bool IsAttachmentDebitBypassAuthorized()
        {
            var configuredKey = BillingOptions.AttachmentDebitBypassKey?.Trim();
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
            using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes("comparison-key"));
            var leftHash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(left));
            var rightHash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(right));
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
        }

        private static string Fingerprint(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "<missing>";
            var raw = value;
            var trimmed = value.Trim();
            var hadWs = raw.Length != trimmed.Length;
            if (trimmed.Length == 0) return $"<whitespace-only len={raw.Length}>";
            var head = trimmed.Length >= 4 ? trimmed[..4] : trimmed;
            var tail = trimmed.Length >= 8 ? trimmed[^4..] : "";
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(trimmed)))[..8];
            return $"{head}…{tail} len={trimmed.Length} sha256:{hash}{(hadWs ? " (had-ws)" : "")}";
        }
    }
}
