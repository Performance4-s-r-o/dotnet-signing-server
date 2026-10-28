using DotNetSigningServer.Filters;
using DotNetSigningServer.Data;
using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Operations;
using iText.Signatures;
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
        private readonly PdfLtvService _ltvService;
        private readonly IDataProtector _dataProtector;
        internal const string AttachmentDebitBypassHeader = "X-P4PDF-Attachment-Billing-Bypass";
        private const string SigningDataNotFoundCode = "SIGNING_DATA_NOT_FOUND";
        /// <summary>Set on a raw PDF answer of <c>/api/sign</c>; the JSON answer carries <c>signatureVerified</c>.</summary>
        public const string SignatureVerifiedHeader = "X-Signature-Verified";
        /// <summary>Attempts of <c>/api/sign</c> per presign; an invalid signature on the last one removes the presign.</summary>
        internal const int MaxSignAttemptsPerPresign = 3;

        public PdfSigningApiController(
            ApplicationDbContext dbContext,
            PdfSigningService signingService,
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
            _ltvService = ltvService;
            _dataProtector = dataProtectionProvider.CreateProtector("SigningData.TsaCredentials");
        }

        [CapacityPool(CapacityPoolKind.Heavy)]
        [HttpPost("/api/presign")]
        public async Task<IActionResult> PreSign([FromBody] PreSignInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            byte[] pdf;
            try
            {
                pdf = GuardedPdfFromBase64(input.PdfContent, "Presign");
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
            }
            catch (ApiValidationException ex)
            {
                return InputRefused(ex);
            }
            // Dál nesou dokument bajty; řetězec (2× velikost v UTF-16) už nikdo nepotřebuje.
            input.PdfContent = string.Empty;
            return await PreSignCoreAsync(user, input, pdf);
        }

        /// <summary>
        /// <c>/api/presign</c> v interním binárním kanálu (#74): <c>multipart/form-data</c> s částmi
        /// <c>metadata</c> a <c>pdf</c>. Stejné ověření, kontroly i odpověď jako JSON varianta.
        /// Tělo čte jen <see cref="ApiControllerBase.ReadBinaryPdfAsync{T}"/> — <c>Request.Form</c>
        /// se tu číst nesmí (nabufferoval by celé tělo bez limitu části).
        /// </summary>
        [CapacityPool(CapacityPoolKind.Heavy)]
        [HttpPost("/api/presign")]
        [Consumes(BinaryPdfChannel.ContentType)]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<IActionResult> PreSignBinary()
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            var (input, pdf, refused) = await ReadBinaryPdfAsync<PreSignInput>("Presign", i => i.PdfContent);
            if (refused != null) return refused;
            try
            {
                SafetyGuard.EnsureSafePdf(pdf!, "Presign");
                LimitGuard.EnsureImageWithinLimit(input!.SignImageContent, "Signature image");
            }
            catch (ApiValidationException ex)
            {
                return InputRefused(ex);
            }
            return await PreSignCoreAsync(user, input!, pdf!);
        }

        /// <summary>Presign po vstupní kontrole, společný oběma kanálům.</summary>
        private async Task<IActionResult> PreSignCoreAsync(User user, PreSignInput input, byte[] pdf)
        {
            var maxPending = HttpContext.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.MaxPendingPresignsPerUser;
            if (maxPending > 0 && !PortalTrust.IsTrustedCaller(HttpContext) && await DbContext.SigningData.CountAsync(x => x.UserId == user.Id) >= maxPending)
            {
                // Each presign is a PDF on disk until it is signed or expires (#74). The portal is exempt: one account for all tenants.
                Response.Headers.RetryAfter = "60";
                return StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    code = "TOO_MANY_PENDING_PRESIGNS",
                    message = Localizer["Error_TOO_MANY_PENDING_PRESIGNS"].Value,
                });
            }

            string? writtenPath = null;
            string? presignId = null;
            try
            {
                var signingData = new SigningData();
                presignId = signingData.Id;
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

                var (presignedPdfPath, hashToSign) = _signingService.HandlePreSign(input, pdf, fieldName);
                writtenPath = presignedPdfPath;
                signingData.FieldName = fieldName;
                signingData.PresignedPdfPath = presignedPdfPath;
                signingData.HashToSign = hashToSign;
                signingData.CertificatePem = input.CertificatePem;
                signingData.TsaUrl = input.TsaUrl;
                signingData.TsaUsername = !string.IsNullOrEmpty(input.TsaUsername)
                    ? _dataProtector.Protect(input.TsaUsername) : null;
                signingData.TsaPassword = !string.IsNullOrEmpty(input.TsaPassword)
                    ? _dataProtector.Protect(input.TsaPassword) : null;
                // The backup is stored like the primary: /api/sign reads the TSA
                // from here, never from its own request.
                signingData.TsaBackupUrl = string.IsNullOrWhiteSpace(input.TsaBackupUrl) ? null : input.TsaBackupUrl;
                signingData.TsaBackupUsername = !string.IsNullOrEmpty(input.TsaBackupUsername)
                    ? _dataProtector.Protect(input.TsaBackupUsername) : null;
                signingData.TsaBackupPassword = !string.IsNullOrEmpty(input.TsaBackupPassword)
                    ? _dataProtector.Protect(input.TsaBackupPassword) : null;

                signingData.UserId = user.Id;
                // The portal's tenant and request id (#78); null for anybody else, whatever they sent.
                var caller = PortalTrust.CallerOf(HttpContext);
                signingData.TenantId = caller.TenantId;
                signingData.RequestId = caller.RequestId;

                DbContext.SigningData.Add(signingData);
                await DbContext.SaveChangesAsync();
                writtenPath = null; // the row now owns the file

                return Ok(new { id = signingData.Id, hashToSign = signingData.HashToSign });
            }
            catch (Exception ex)
            {
                // The PDF was written before the row: if the row never made it, nothing else
                // knows the file exists and nothing would ever remove it.
                if (writtenPath != null)
                {
                    try
                    {
                        // A commit that went through and then lost the connection leaves a row
                        // that names this file; deleting the file then would orphan the row.
                        var committed = presignId != null
                            && await DbContext.SigningData.AsNoTracking().AnyAsync(x => x.Id == presignId);
                        if (!committed) System.IO.File.Delete(writtenPath);
                    }
                    catch { /* the sweep in PresignCleanupService is the backstop */ }
                }
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Presign failed");
                return SafeProblem(Localizer["PresignError"], ex);
            }
        }

        [CapacityPool(CapacityPoolKind.Heavy)]
        [HttpPost("/api/sign")]
        public async Task<IActionResult> Sign([FromBody] SignInput input)
        {
            // No credit pre-check: a retry of a completed signature is answered from the stored
            // result even when the balance has run out since. The reservation decides (402).
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            // Ownership before an operation can exist for this presign. Once it completed the
            // presign is gone and the operation itself carries the owner.
            var existing = await Operations.FindByPresignAsync(input.Id);
            var presign = await DbContext.SigningData.AsNoTracking().FirstOrDefaultAsync(x => x.Id == input.Id);
            if (existing == null)
            {
                if (presign == null)
                {
                    return NotFound(new { message = Localizer["SigningDataNotFound"].Value });
                }
                if (presign.UserId != user.Id)
                {
                    return Forbid();
                }
            }
            else if (existing.UserId != user.Id)
            {
                return Forbid();
            }

            // The portal holds one account for every tenant (#78): the owner check above cannot tell
            // one tenant's presign from another's. A presign made for a tenant is signed for that
            // tenant only; one made without (older presigns, other callers) is not checked. Once the
            // signature is done the presign is gone and the operation carries the tenant: a replay
            // for another tenant is refused too.
            var callerTenant = PortalTrust.CallerOf(HttpContext).TenantId;
            if (presign?.TenantId != null && !string.Equals(presign.TenantId, callerTenant, StringComparison.Ordinal))
            {
                return TenantMismatch($"presign {input.Id}");
            }
            if (existing != null && PortalTrust.TenantsConflict(existing.TenantId, callerTenant))
            {
                return TenantMismatch($"presign {input.Id}");
            }

            // The presign is NOT deleted before the work any more (#77): the operation row,
            // unique per presign, is what lets only one /api/sign do it. A failure leaves the
            // presign in place so the retry goes through; success removes it.
            string? presignedPdfPath = null;
            try
            {
                return await RunOperationAsync(
                    user,
                    "sign",
                    // The presign is the identity: a client that signs the same hash again
                    // (ECDSA is randomised) is still the same signature.
                    ApiOperationService.Fingerprint("sign", input.Id),
                    baseCost: 1,
                    work: async _ =>
                    {
                        var signingData = await DbContext.SigningData.AsNoTracking().FirstOrDefaultAsync(x => x.Id == input.Id)
                            ?? throw new ApiValidationException(SigningDataNotFoundCode);
                        presignedPdfPath = signingData.PresignedPdfPath;

                        var tsaUsername = !string.IsNullOrEmpty(signingData.TsaUsername)
                            ? _dataProtector.Unprotect(signingData.TsaUsername) : null;
                        var tsaPassword = !string.IsNullOrEmpty(signingData.TsaPassword)
                            ? _dataProtector.Unprotect(signingData.TsaPassword) : null;
                        var tsaBackup = TsaEndpoint.From(
                            signingData.TsaBackupUrl,
                            !string.IsNullOrEmpty(signingData.TsaBackupUsername)
                                ? _dataProtector.Unprotect(signingData.TsaBackupUsername) : null,
                            !string.IsNullOrEmpty(signingData.TsaBackupPassword)
                                ? _dataProtector.Unprotect(signingData.TsaBackupPassword) : null);

                        var result = _signingService.HandleSign(
                            input,
                            signingData.PresignedPdfPath,
                            signingData.CertificatePem,
                            signingData.FieldName,
                            signingData.TsaUrl,
                            tsaUsername,
                            tsaPassword,
                            tsaBackup);
                        // HandleSign embeds only a signature that verified (#81); the binary channel gets it as a header.
                        return new PaidResult(
                            new { result, signatureVerified = true },
                            result,
                            new Dictionary<string, string> { [SignatureVerifiedHeader] = "true" });
                    },
                    presignId: input.Id,
                    afterCompleted: async () =>
                    {
                        await DbContext.SigningData.Where(x => x.Id == input.Id).ExecuteDeleteAsync();
                        if (presignedPdfPath != null)
                        {
                            try { System.IO.File.Delete(presignedPdfPath); }
                            catch { /* best-effort; PresignCleanupService is the backstop */ }
                        }
                    });
            }
            catch (ApiValidationException ex) when (ex.Code == SigningDataNotFoundCode)
            {
                // Presign expired between the check and the work.
                return NotFound(new { message = Localizer["SigningDataNotFound"].Value });
            }
            catch (SignatureInvalidException ex)
            {
                return await SignatureInvalidResultAsync(input.Id, ex);
            }
            catch (TsaCommunicationException ex)
            {
                Logger.LogWarning(ex, "Sign failed: TSA communication error ({TsaUrl})", ex.TsaUrl);
                return TsaUnreachableResult(ex);
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Sign failed");
                return SafeProblem(Localizer["SignError"], ex);
            }
        }

        /// <summary>
        /// 422 for a client signature that does not verify (#81). The operation is already
        /// <c>failed</c> with its reservation returned; the presign stays for a corrected
        /// signature until <see cref="MaxSignAttemptsPerPresign"/> attempts, then it is removed.
        /// </summary>
        private async Task<IActionResult> SignatureInvalidResultAsync(string presignId, SignatureInvalidException ex)
        {
            // This request's own attempt, not a re-read: a concurrent claim must not move it.
            var attempts = HttpContext.Items[OperationAttemptItemKey] is int own ? own : 0;
            var remaining = Math.Max(0, MaxSignAttemptsPerPresign - attempts);
            Logger.LogWarning("Sign refused for presign {PresignId}: client signature invalid ({Detail}); attempt {Attempt}",
                presignId, ex.Message, attempts);
            if (remaining == 0)
            {
                // Out of attempts: the presign goes, so a caller cannot keep probing it.
                var path = await DbContext.SigningData.Where(x => x.Id == presignId)
                    .Select(x => x.PresignedPdfPath).FirstOrDefaultAsync();
                await DbContext.SigningData.Where(x => x.Id == presignId).ExecuteDeleteAsync();
                if (path != null)
                {
                    try { System.IO.File.Delete(path); }
                    catch { /* best-effort; PresignCleanupService is the backstop */ }
                }
            }
            return UnprocessableEntity(new
            {
                code = SignatureInvalidException.ErrorCode,
                message = Localizer[$"Error_{SignatureInvalidException.ErrorCode}"].Value,
                reason = ex.Reason,
                attemptsRemaining = remaining,
            });
        }

        [CapacityPool(CapacityPoolKind.Heavy)]
        [HttpPost("/api/sign-pfx")]
        public async Task<IActionResult> SignWithPfx([FromBody] PfxSignInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Sign with PFX");
                EnsurePfxSafe(input.PfxContent);
                SafetyGuard.EnsureSafePdf(input.PdfContent, "Sign with PFX");
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            return await SignWithPfxCoreAsync(user, input, null, ApiOperationService.Fingerprint("sign-pfx", input));
        }

        /// <summary>
        /// <c>/api/sign-pfx</c> v interním binárním kanálu (#74): <c>multipart/form-data</c> s částmi
        /// <c>metadata</c> (PFX a heslo zůstávají v metadatech) a <c>pdf</c>. Stejné kontroly,
        /// účtování i odpověď jako JSON varianta; <c>Request.Form</c> se tu číst nesmí.
        /// </summary>
        [CapacityPool(CapacityPoolKind.Heavy)]
        [HttpPost("/api/sign-pfx")]
        [Consumes(BinaryPdfChannel.ContentType)]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<IActionResult> SignWithPfxBinary()
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            var (input, pdf, refused) = await ReadBinaryPdfAsync<PfxSignInput>("Sign with PFX", i => i.PdfContent);
            if (refused != null) return refused;
            try
            {
                EnsurePfxSafe(input!.PfxContent);
                SafetyGuard.EnsureSafePdf(pdf!, "Sign with PFX");
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
            }
            catch (ApiValidationException ex)
            {
                return InputRefused(ex);
            }

            return await SignWithPfxCoreAsync(user, input, pdf, BinaryFingerprint("sign-pfx", input, pdf!));
        }

        private static void EnsurePfxSafe(string pfxContent)
        {
            try
            {
                PfxGuard.EnsureSafe(Convert.FromBase64String(pfxContent));
            }
            catch (FormatException)
            {
                // Not base64: the signing service answers that with its own message.
            }
        }

        /// <summary>sign-pfx po vstupní kontrole, společné oběma kanálům; <c>pdf == null</c> = dokument je v <c>input.PdfContent</c>.</summary>
        private async Task<IActionResult> SignWithPfxCoreAsync(User user, PfxSignInput input, byte[]? pdf, string fingerprint)
        {
            try
            {
                return await RunOperationAsync(user, "sign-pfx", fingerprint, baseCost: 1, async _ =>
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
                    var result = pdf == null ? _signingService.SignWithPfx(input) : _signingService.SignWithPfx(input, pdf);
                    return new PaidResult(new { result }, result);
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "PFX sign failed");
                return SafeProblem(Localizer["PfxSignError"], ex);
            }
        }

        [CapacityPool(CapacityPoolKind.Heavy)]
        [HttpPost("/api/timestamp")]
        public async Task<IActionResult> ApplyTimestamp([FromBody] DocumentTimestampInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Timestamp");
                SafetyGuard.EnsureSafePdf(input.PdfContent, "Timestamp");
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            return await TimestampCoreAsync(user, input, null, ApiOperationService.Fingerprint("timestamp", input));
        }

        /// <summary><c>/api/timestamp</c> v interním binárním kanálu (#74), viz <see cref="SignWithPfxBinary"/>.</summary>
        [CapacityPool(CapacityPoolKind.Heavy)]
        [HttpPost("/api/timestamp")]
        [Consumes(BinaryPdfChannel.ContentType)]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<IActionResult> ApplyTimestampBinary()
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            var (input, pdf, refused) = await ReadBinaryPdfAsync<DocumentTimestampInput>("Timestamp", i => i.PdfContent);
            if (refused != null) return refused;
            try
            {
                SafetyGuard.EnsureSafePdf(pdf!, "Timestamp");
                LimitGuard.EnsureImageWithinLimit(input!.SignImageContent, "Signature image");
            }
            catch (ApiValidationException ex)
            {
                return InputRefused(ex);
            }

            return await TimestampCoreAsync(user, input, pdf, BinaryFingerprint("timestamp", input, pdf!));
        }

        /// <summary>timestamp po vstupní kontrole, společné oběma kanálům; <c>pdf == null</c> = dokument je v <c>input.PdfContent</c>.</summary>
        private async Task<IActionResult> TimestampCoreAsync(User user, DocumentTimestampInput input, byte[]? pdf, string fingerprint)
        {
            try
            {
                return await RunOperationAsync(user, "timestamp", fingerprint, baseCost: 1, async _ =>
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
                    var result = pdf == null ? _signingService.ApplyDocumentTimestamp(input) : _signingService.ApplyDocumentTimestamp(input, pdf);
                    return new PaidResult(new { result }, result);
                });
            }
            catch (TsaCommunicationException ex)
            {
                Logger.LogWarning(ex, "Timestamp failed: TSA communication error ({TsaUrl})", ex.TsaUrl);
                return TsaUnreachableResult(ex);
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
        [CapacityPool(CapacityPoolKind.Heavy)]
        [HttpPost("/api/extend-signature")]
        public async Task<IActionResult> ExtendSignature([FromBody] ExtendSignatureInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Extend signature");
                SafetyGuard.EnsureSafePdf(input.PdfContent, "Extend signature");
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            return await ExtendSignatureCoreAsync(user, input, null, ApiOperationService.Fingerprint("extend-signature", input));
        }

        /// <summary><c>/api/extend-signature</c> v interním binárním kanálu (#74), viz <see cref="SignWithPfxBinary"/>.</summary>
        [CapacityPool(CapacityPoolKind.Heavy)]
        [HttpPost("/api/extend-signature")]
        [Consumes(BinaryPdfChannel.ContentType)]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<IActionResult> ExtendSignatureBinary()
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            var (input, pdf, refused) = await ReadBinaryPdfAsync<ExtendSignatureInput>("Extend signature", i => i.PdfContent);
            if (refused != null) return refused;
            try
            {
                SafetyGuard.EnsureSafePdf(pdf!, "Extend signature");
            }
            catch (ApiValidationException ex)
            {
                return InputRefused(ex);
            }

            return await ExtendSignatureCoreAsync(user, input!, pdf, BinaryFingerprint("extend-signature", input!, pdf!));
        }

        /// <summary>extend-signature po vstupní kontrole, společné oběma kanálům; <c>pdf == null</c> = dokument je v <c>input.PdfContent</c>.</summary>
        private async Task<IActionResult> ExtendSignatureCoreAsync(User user, ExtendSignatureInput input, byte[]? pdf, string fingerprint)
        {
            iText.Signatures.ITSAClient? tsaClient;
            try
            {
                tsaClient = PdfCryptoHelper.CreateTsaClient(
                    input.TsaUrl, input.TsaUsername, input.TsaPassword,
                    TsaEndpoint.From(input.TsaBackupUrl, input.TsaBackupUsername, input.TsaBackupPassword));
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
                return await RunOperationAsync(user, "extend-signature", fingerprint, baseCost: 1, _ =>
                {
                    var pdfBytes = pdf ?? Convert.FromBase64String(input.PdfContent);
                    var extended = Convert.ToBase64String(_ltvService.Extend(pdfBytes, tsaClient, input.AddArchiveTimestamp));
                    return Task.FromResult(new PaidResult(new { result = extended }, extended));
                });
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
                return TsaUnreachableResult(ex);
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

            // No platform seal exists; the field only reports that.
            return Ok(new ServerCapabilities { Seal = new SealCapability { Enabled = false } });
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
                SafetyGuard.EnsureSafePdf(input.PdfContent, "Signature inspection", checkActiveContent: false);
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

            return await InspectSignaturesCore(pdfBytes);
        }

        /// <summary><c>/api/inspect-signatures</c> v interním binárním kanálu (#74), viz <see cref="SignWithPfxBinary"/>.</summary>
        [HttpPost("/api/inspect-signatures")]
        [Consumes(BinaryPdfChannel.ContentType)]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<IActionResult> InspectSignaturesBinary()
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            var (_, pdf, refused) = await ReadBinaryPdfAsync<InspectSignaturesInput>("Signature inspection", i => i.PdfContent);
            if (refused != null) return refused;
            if (pdf!.Length == 0)
            {
                return BadRequest(new { message = Localizer["PdfContentRequired"].Value });
            }
            try
            {
                SafetyGuard.EnsureSafePdf(pdf, "Signature inspection", checkActiveContent: false);
            }
            catch (ApiValidationException ex)
            {
                return InputRefused(ex);
            }

            return await InspectSignaturesCore(pdf);
        }

        /// <summary>Time allowed for one inspection; a light, free operation (#72).</summary>
        internal static TimeSpan InspectDeadline { get; set; } = TimeSpan.FromSeconds(15);

        internal async Task<IActionResult> InspectSignaturesCore(byte[] pdfBytes)
        {
            var aborted = HttpContext?.RequestAborted ?? CancellationToken.None;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            limit.CancelAfter(InspectDeadline);
            try
            {
                // Off the request thread, so the answer does not depend on the parser noticing the token.
                var work = Task.Run(() => PdfSignatureInspector.Inspect(pdfBytes, limit.Token), CancellationToken.None);
                _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return Ok(await work.WaitAsync(limit.Token));
            }
            catch (OperationCanceledException) when (aborted.IsCancellationRequested)
            {
                // The caller is gone; nobody reads the answer.
                return new EmptyResult();
            }
            catch (OperationCanceledException)
            {
                Logger.LogWarning("Signature inspection exceeded {Seconds} s", InspectDeadline.TotalSeconds);
                return StatusCode(StatusCodes.Status504GatewayTimeout, new
                {
                    code = "INSPECT_DEADLINE_EXCEEDED",
                    message = Localizer["Error_INSPECT_DEADLINE_EXCEEDED"].Value,
                });
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
                // The detail goes to the log only: the probe is free and the URL is the caller's.
                Logger.LogInformation(ex, "TSA probe failed for {TsaUrl}", ex.TsaUrl);
                return Ok(new { ok = false, code = "TSA_UNREACHABLE", message = Localizer["Error_TSA_UNREACHABLE"].Value, tsaUrl = ex.TsaUrl });
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "TSA probe unexpected failure for {TsaUrl}", input.TsaUrl);
                return Ok(new { ok = false, code = "TSA_PROBE_FAILED", message = Localizer["Error_TSA_PROBE_FAILED"].Value, tsaUrl = input.TsaUrl });
            }
        }

        /// <summary>
        /// RFC 3161 token over a SHA-256 digest the caller already holds (no document is sent).
        /// Free, like <c>inspect-signatures</c>; the authority comes from the request and faces the
        /// same checks as on every other timestamp.
        /// </summary>
        [HttpPost("/api/timestamp-digest")]
        public async Task<IActionResult> TimestampDigest([FromBody] TimestampDigestInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            byte[] digest;
            ITSAClient client;
            try
            {
                digest = TimestampDigestService.ParseDigest(input?.Digest);
                client = TimestampDigestService.CreateClient(input!);
            }
            catch (ApiValidationException ex)
            {
                return InputRefused(ex);
            }

            if (!TimestampDigestService.Gate.Wait(0))
            {
                Response.Headers.RetryAfter = "1";
                return StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    code = "TIMESTAMP_DIGEST_BUSY",
                    message = Localizer["Error_TIMESTAMP_DIGEST_BUSY"].Value,
                });
            }

            var tsaUrl = input!.TsaUrl!;
            var aborted = HttpContext?.RequestAborted ?? CancellationToken.None;
            // Off the request thread: the client is synchronous and the authority may be slow.
            var work = Task.Run(() =>
            {
                try
                {
                    return TimestampDigestService.Stamp(client, digest, tsaUrl);
                }
                finally
                {
                    TimestampDigestService.Gate.Release();
                }
            });
            _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

            try
            {
                return Ok(await work.WaitAsync(TimestampDigestService.Deadline, aborted));
            }
            catch (OperationCanceledException) when (aborted.IsCancellationRequested)
            {
                return new EmptyResult();
            }
            catch (TimeoutException)
            {
                Logger.LogWarning("Timestamp digest: the authority did not answer within {Seconds} s ({TsaUrl})",
                    TimestampDigestService.Deadline.TotalSeconds, tsaUrl);
                return TsaUnreachableResult(new TsaCommunicationException(tsaUrl, "The timestamp authority did not answer in time."));
            }
            catch (TsaCommunicationException ex)
            {
                Logger.LogWarning(ex, "Timestamp digest failed: TSA communication error ({TsaUrl})", ex.TsaUrl);
                return TsaUnreachableResult(ex);
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Timestamp digest failed");
                return SafeProblem(Localizer["TimestampDigestError"], ex);
            }
        }

        [CapacityPool(CapacityPoolKind.Heavy)]
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

            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null)
            {
                Logger.LogWarning("[visual-sign] Auth/credits check failed, user={UserId}, error type={ErrorType}", user?.Id, error?.GetType().Name);
                return error!;
            }
            Logger.LogInformation("[visual-sign] Authenticated as {UserId}", user.Id);

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Visual sign");
                SafetyGuard.EnsureSafePdf(input.PdfContent, "Visual sign");
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
            }
            catch (ApiValidationException ex)
            {
                Logger.LogWarning("[visual-sign] Limit check failed: {Code}", ex.Code);
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            return await VisualSignCoreAsync(user, input, null, ApiOperationService.Fingerprint("visual-sign", input));
        }

        /// <summary><c>/api/visual-sign</c> v interním binárním kanálu (#74), viz <see cref="SignWithPfxBinary"/>.</summary>
        [CapacityPool(CapacityPoolKind.Heavy)]
        [HttpPost("/api/visual-sign")]
        [Consumes(BinaryPdfChannel.ContentType)]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<IActionResult> ApplyVisualSignBinary()
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            var (input, pdf, refused) = await ReadBinaryPdfAsync<VisualSignInput>("Visual sign", i => i.PdfContent);
            if (refused != null) return refused;
            try
            {
                SafetyGuard.EnsureSafePdf(pdf!, "Visual sign");
                LimitGuard.EnsureImageWithinLimit(input!.SignImageContent, "Signature image");
            }
            catch (ApiValidationException ex)
            {
                return InputRefused(ex);
            }

            return await VisualSignCoreAsync(user, input, pdf, BinaryFingerprint("visual-sign", input, pdf!));
        }

        /// <summary>visual-sign po vstupní kontrole, společné oběma kanálům; <c>pdf == null</c> = dokument je v <c>input.PdfContent</c>.</summary>
        private async Task<IActionResult> VisualSignCoreAsync(User user, VisualSignInput input, byte[]? pdf, string fingerprint)
        {
            try
            {
                return await RunOperationAsync(user, "visual-sign", fingerprint, baseCost: 1, async _ =>
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
                    var result = pdf == null ? _signingService.ApplyVisualSign(input) : _signingService.ApplyVisualSign(input, pdf);
                    Logger.LogInformation("[visual-sign] Success, result length={Len}", result?.Length ?? 0);
                    return new PaidResult(new { result }, result);
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "[visual-sign] Failed: {Message}", ex.Message);
                return SafeProblem(Localizer["VisualSignError"], ex);
            }
        }

        /// <summary>
        /// The server has no seal certificate of its own (#122): the caller supplies
        /// the certificate (presign/sign with its key, or sign-pfx). This endpoint
        /// stays only to answer with an explicit error instead of a 404 and never
        /// falls back to another key. The body is not read.
        /// </summary>
        [HttpPost("/api/seal")]
        public async Task<IActionResult> ApplySeal()
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (!user.SealAllowed)
            {
                return SealNotPermitted(user);
            }

            return StatusCode(
                StatusCodes.Status422UnprocessableEntity,
                new { code = "SEAL_CERTIFICATE_REQUIRED", message = Localizer["SealCertificateRequired"].Value });
        }

        private IActionResult SealNotPermitted(User user)
        {
            Logger.LogWarning(
                Logging.LoggingEvents.AuthFailed,
                "Seal refused for user {UserId} — SealAllowed is not granted",
                user.Id);
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { code = "SEAL_NOT_PERMITTED", message = Localizer["SealNotPermitted"].Value });
        }

        [HttpPost("/api/attachment")]
        public async Task<IActionResult> AddAttachment([FromBody] AddAttachmentInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            var bypassDebitRequested = BypassDebitRequested();
            if (!AttachmentBypassAllowed(user, bypassDebitRequested))
            {
                return Forbid();
            }

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Attachment PDF");
                SafetyGuard.EnsureSafePdf(input.PdfContent, "Attachment PDF");
                LimitGuard.EnsureAttachmentWithinLimit(input.AttachmentContent, "Attachment");
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            return await AttachmentCoreAsync(user, input, null, bypassDebitRequested,
                ApiOperationService.Fingerprint("attachment", input, bypassDebitRequested));
        }

        /// <summary>
        /// <c>/api/attachment</c> v interním binárním kanálu (#74), viz <see cref="SignWithPfxBinary"/>.
        /// Příloha (<c>attachmentContent</c>) zůstává v metadatech v base64; strop metadat o ni roste
        /// o <c>Limits:AttachmentMaxBytes</c>.
        /// </summary>
        [HttpPost("/api/attachment")]
        [Consumes(BinaryPdfChannel.ContentType)]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<IActionResult> AddAttachmentBinary()
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            var bypassDebitRequested = BypassDebitRequested();
            if (!AttachmentBypassAllowed(user, bypassDebitRequested))
            {
                return Forbid();
            }

            var (input, pdf, refused) = await ReadBinaryPdfAsync<AddAttachmentInput>(
                "Attachment PDF", i => i.PdfContent, extraMetadataBytes: LimitGuard.Limits.AttachmentMaxBytes / 3 * 4 + 1024);
            if (refused != null) return refused;
            try
            {
                SafetyGuard.EnsureSafePdf(pdf!, "Attachment PDF");
                LimitGuard.EnsureAttachmentWithinLimit(input!.AttachmentContent, "Attachment");
            }
            catch (ApiValidationException ex)
            {
                return InputRefused(ex);
            }

            return await AttachmentCoreAsync(user, input, pdf, bypassDebitRequested,
                BinaryFingerprint("attachment", input, pdf!, bypassDebitRequested));
        }

        private bool BypassDebitRequested() =>
            Request.Headers.ContainsKey(AttachmentDebitBypassHeader);

        private bool AttachmentBypassAllowed(User user, bool bypassDebitRequested)
        {
            // Never log any part of the bypass key — not the caller's, not the
            // configured one. head+tail+length+hash-prefix of a shared secret, emitted
            // on every request and shipped to Loki, narrows brute-force considerably.
            Logger.LogInformation(
                "[attachment] user={UserId} bypassHeaderPresent={HeaderPresent} bypassKeyConfigured={KeyConfigured}",
                user.Id,
                bypassDebitRequested,
                !string.IsNullOrWhiteSpace(BillingOptions.AttachmentDebitBypassKey));
            if (bypassDebitRequested && !IsAttachmentDebitBypassAuthorized(HttpContext, BillingOptions.AttachmentDebitBypassKey))
            {
                Logger.LogWarning("Attachment billing bypass rejected for user {UserId}", user.Id);
                return false;
            }
            return true;
        }

        /// <summary>attachment po vstupní kontrole, společné oběma kanálům; <c>pdf == null</c> = dokument je v <c>input.PdfContent</c>.</summary>
        private async Task<IActionResult> AttachmentCoreAsync(User user, AddAttachmentInput input, byte[]? pdf, bool bypassDebitRequested, string fingerprint)
        {
            try
            {
                return await RunOperationAsync(
                    user,
                    "attachment",
                    fingerprint,
                    baseCost: bypassDebitRequested ? 0 : 1,
                    _ =>
                    {
                        var result = pdf == null ? _signingService.AddAttachment(input) : _signingService.AddAttachment(input, pdf);
                        return Task.FromResult(new PaidResult(new { result }, result));
                    });
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Add attachment failed");
                return SafeProblem(Localizer["AttachmentError"], ex);
            }
        }

        /// <summary>
        /// The portal's token (#78) with the shared key, in the X-P4PDF-Attachment-Billing-Bypass header. Any other token
        /// is refused even with the right key: the key alone is a secret that can leak, the token
        /// on the internal listener is the portal.
        /// </summary>
        internal static bool IsAttachmentDebitBypassAuthorized(HttpContext context, string? configuredBypassKey)
        {
            if (!PortalTrust.IsTrustedCaller(context))
            {
                return false;
            }

            var configuredKey = configuredBypassKey?.Trim();
            if (string.IsNullOrWhiteSpace(configuredKey))
            {
                return false;
            }

            var providedKey = context.Request.Headers[AttachmentDebitBypassHeader].ToString().Trim();
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
