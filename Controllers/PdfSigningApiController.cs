using DotNetSigningServer.Data;
using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Controllers
{
    [Route("api")]
    public class PdfSigningApiController : ApiControllerBase
    {
        private readonly PdfSigningService _signingService;
        private readonly PdfSealingService _sealingService;
        private readonly PdfLtvService _ltvService;
        private readonly IDataProtector _dataProtector;
        private const string AttachmentDebitBypassHeader = "X-P4PDF-Attachment-Billing-Bypass";

        public PdfSigningApiController(
            ApplicationDbContext dbContext,
            PdfSigningService signingService,
            PdfSealingService sealingService,
            PdfLtvService ltvService,
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
            _ltvService = ltvService;
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
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
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

            // Atomic claim: remove the presign row immediately so only ONE
            // concurrent /api/sign can consume it. A losing double-submit gets 0
            // rows and bails here — no double-sign / double-charge. (The in-memory
            // `signingData` still holds the path/cert/hash we need to sign with.)
            var claimed = await DbContext.SigningData
                .Where(x => x.Id == input.Id)
                .ExecuteDeleteAsync();
            if (claimed == 0)
            {
                return Conflict(new { message = Localizer["SigningDataNotFound"].Value });
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

                // Charge BEFORE delivering. If the balance raced to zero since the
                // pre-check, or the concurrency tier multiplied the cost beyond it,
                // the atomic debit returns false — don't hand over a free signature.
                var debited = await DebitUserAsync(user);
                if (!debited)
                {
                    return StatusCode(StatusCodes.Status402PaymentRequired,
                        new { message = Localizer["NoCreditsRemaining"].Value });
                }

                try { System.IO.File.Delete(signingData.PresignedPdfPath); }
                catch { /* best-effort; PresignCleanupService is the backstop */ }

                return PdfOrJsonResult(result);
            }
            catch (TsaCommunicationException ex)
            {
                Logger.LogWarning(ex, "Sign failed: TSA communication error ({TsaUrl})", ex.TsaUrl);
                return StatusCode(StatusCodes.Status502BadGateway, new { code = "TSA_UNREACHABLE", message = ex.Message, tsaUrl = ex.TsaUrl });
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
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
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
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
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
            catch (TsaCommunicationException ex)
            {
                Logger.LogWarning(ex, "Timestamp failed: TSA communication error ({TsaUrl})", ex.TsaUrl);
                return StatusCode(StatusCodes.Status502BadGateway, new { code = "TSA_UNREACHABLE", message = ex.Message, tsaUrl = ex.TsaUrl });
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Timestamp failed");
                return SafeProblem(Localizer["TimestampError"], ex);
            }
        }

        /// <summary>
        /// Raises an already-signed PDF to PAdES B-LT, or B-LTA when an archive
        /// timestamp is asked for. Same call is used to enrol a document and to
        /// renew it years later.
        /// </summary>
        [HttpPost("/api/extend-signature")]
        public async Task<IActionResult> ExtendSignature([FromBody] ExtendSignatureInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Extend signature");
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            iText.Signatures.ITSAClient? tsaClient;
            try
            {
                tsaClient = PdfCryptoHelper.CreateTsaClient(input.TsaUrl, input.TsaUsername, input.TsaPassword);
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            if (input.AddArchiveTimestamp && tsaClient == null)
            {
                // Silently downgrading to B-LT would hand back a document that
                // claims less than the caller asked for, and a renewal that quietly
                // adds no timestamp is exactly the failure this feature exists to
                // prevent.
                return BadRequest(new
                {
                    code = "TSA_REQUIRED_FOR_ARCHIVE_TIMESTAMP",
                    message = Localizer["Error_TSA_REQUIRED_FOR_ARCHIVE_TIMESTAMP"].Value,
                });
            }

            try
            {
                var pdfBytes = Convert.FromBase64String(input.PdfContent);
                var extended = _ltvService.Extend(pdfBytes, tsaClient, input.AddArchiveTimestamp);

                var debited = await DebitUserAsync(user);
                if (!debited)
                {
                    return StatusCode(StatusCodes.Status402PaymentRequired,
                        new { message = Localizer["NoCreditsRemaining"].Value });
                }

                return PdfOrJsonResult(Convert.ToBase64String(extended));
            }
            catch (FormatException)
            {
                return BadRequest(new { message = Localizer["InvalidBase64"].Value });
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }
            catch (TsaCommunicationException ex)
            {
                Logger.LogWarning(ex, "Extend signature failed: TSA communication error ({TsaUrl})", ex.TsaUrl);
                return StatusCode(StatusCodes.Status502BadGateway, new { code = "TSA_UNREACHABLE", message = ex.Message, tsaUrl = ex.TsaUrl });
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Extend signature failed");
                return SafeProblem(Localizer["ExtendSignatureError"], ex);
            }
        }

        /// <summary>
        /// Reports what signatures a PDF already carries and when their timestamp
        /// certificates expire. Free: a renewal watcher has to poll this to know
        /// when a document is due, and charging for the check would make skipping
        /// it the cheaper option.
        /// </summary>
        /// <summary>
        /// What this server can do, before a caller commits a user to a flow that
        /// depends on it. Free and side-effect free: a client that had to pay to
        /// ask would simply not ask, which defeats the point.
        /// </summary>
        [HttpGet("/api/capabilities")]
        public async Task<IActionResult> Capabilities()
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            // A caller without the grant learns nothing about the certificate —
            // not that one exists, not whose it is. Reporting it would hand a
            // would-be forger the subject to aim at, and the answer is "no" for
            // them either way.
            var seal = user.SealAllowed
                ? _sealingService.DescribeCapability()
                : new SealCapability { Enabled = false };

            return Ok(new ServerCapabilities { Seal = seal });
        }

        [HttpPost("/api/inspect-signatures")]
        public async Task<IActionResult> InspectSignatures([FromBody] InspectSignaturesInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (string.IsNullOrWhiteSpace(input.PdfContent))
            {
                return BadRequest(new { message = Localizer["PdfContentRequired"].Value });
            }

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Signature inspection");
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            byte[] pdfBytes;
            try
            {
                pdfBytes = Convert.FromBase64String(input.PdfContent);
            }
            catch (FormatException)
            {
                return BadRequest(new { message = Localizer["InvalidBase64"].Value });
            }

            try
            {
                return Ok(PdfSignatureInspector.Inspect(pdfBytes));
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Signature inspection failed");
                return SafeProblem(Localizer["InspectSignaturesError"], ex);
            }
        }

        [HttpPost("/api/tsa-probe")]
        public async Task<IActionResult> ProbeTsa([FromBody] TsaProbeInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                _signingService.ProbeTsa(input.TsaUrl, input.TsaUsername, input.TsaPassword);
                return Ok(new { ok = true });
            }
            catch (TsaCommunicationException ex)
            {
                Logger.LogInformation("TSA probe failed for {TsaUrl}: {Message}", ex.TsaUrl, ex.Message);
                return Ok(new { ok = false, code = "TSA_UNREACHABLE", message = ex.Message, tsaUrl = ex.TsaUrl });
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "TSA probe unexpected failure for {TsaUrl}", input.TsaUrl);
                return Ok(new { ok = false, code = "TSA_PROBE_FAILED", message = ex.Message, tsaUrl = input.TsaUrl });
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
            catch (ApiValidationException ex)
            {
                Logger.LogWarning("[visual-sign] Limit check failed: {Code}", ex.Code);
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
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

        /// <summary>
        /// Signs with the operator's own certificate, so a valid token and a
        /// credit balance are not enough — every other endpoint signs with
        /// material the caller supplied, this one lends our identity. Gated on
        /// <see cref="User.SealAllowed"/>, granted per account from /Admin.
        /// </summary>
        [HttpPost("/api/seal")]
        public async Task<IActionResult> ApplySeal([FromBody] SealInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (!user.SealAllowed)
            {
                Logger.LogWarning(
                    Logging.LoggingEvents.AuthFailed,
                    "Seal refused for user {UserId} — SealAllowed is not granted",
                    user.Id);
                return StatusCode(
                    StatusCodes.Status403Forbidden,
                    new { code = "SEAL_NOT_PERMITTED", message = Localizer["SealNotPermitted"].Value });
            }

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Seal");
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature");
                LimitGuard.EnsureImageWithinLimit(input.StampImageContent, "Stamp");
                LimitGuard.EnsureImageWithinLimit(input.CompanyLogoContent, "Company logo");
                LimitGuard.EnsureImageWithinLimit(input.BackgroundImageContent, "Background");
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
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
            // Never log any part of the bypass key — not the caller's, not the
            // configured one. head+tail+length+hash-prefix of a shared secret, emitted
            // on every request and shipped to Loki, narrows brute-force considerably.
            Logger.LogInformation(
                "[attachment] user={UserId} bypassHeaderPresent={HeaderPresent} bypassKeyConfigured={KeyConfigured}",
                user.Id,
                bypassDebitRequested,
                !string.IsNullOrWhiteSpace(BillingOptions.AttachmentDebitBypassKey));
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
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
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

    }
}
