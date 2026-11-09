using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Services.Settings;
using DotNetSigningServer.Filters;
using DotNetSigningServer.Data;
using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.SignatureLayout;
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
                // Active content is what flattening removes; the prepared document gets the full check.
                pdf = GuardedPdfFromBase64(input.PdfContent, "Presign", checkActiveContent: !input.FlattenForm);
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
                LimitGuard.EnsureImageWithinLimit(input.ContentStamp?.SignImageContent, "Signature image");
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
                SafetyGuard.EnsureSafePdf(pdf!, "Presign", checkActiveContent: !input!.FlattenForm);
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
                LimitGuard.EnsureImageWithinLimit(input.ContentStamp?.SignImageContent, "Signature image");
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
            if (input.PlaceBesideContentStamp && input.ContentStamp == null)
            {
                return InputRefused(new ApiValidationException("CONTENT_STAMP_REQUIRED"));
            }
            var maxPending = HttpContext.RequestServices.GetRequiredService<Services.Settings.IRuntimeSettings>().MaxPendingPresignsPerUser();
            if (maxPending > 0 && await DbContext.SigningData.CountAsync(x => x.UserId == user.Id) >= maxPending)
            {
                // Each presign is a PDF on disk until it is signed or expires (#74); per account, so per tenant (#131).
                Response.Headers.RetryAfter = "60";
                return StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    code = "TOO_MANY_PENDING_PRESIGNS",
                    message = Localizer["Error_TOO_MANY_PENDING_PRESIGNS"].Value,
                });
            }

            DocumentPreparation.Result? prepared = null;
            var preparationCredits = 0;
            if (DocumentPreparation.Requested(input))
            {
                var (result, credits, refused) = await PrepareAsync(user, input, pdf, "Presign", "PresignError", checkCredits: true);
                if (refused != null) return refused;
                prepared = result!;
                preparationCredits = credits;
                pdf = prepared.Pdf;
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

                var (presignedPdfPath, hashToSign, stampPlacement) = _signingService.HandlePreSignWithStamp(input, pdf, fieldName, prepared?.KeepPdfA);
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

                signingData.PreparationCredits = preparationCredits;
                signingData.UserId = user.Id;

                DbContext.SigningData.Add(signingData);
                await DbContext.SaveChangesAsync();
                writtenPath = null; // the row now owns the file

                // The caller checks contentStamp: an older server would drop the stamp without a word (portál #511).
                if (prepared == null)
                {
                    return stampPlacement == null
                        ? Ok(new { id = signingData.Id, hashToSign = signingData.HashToSign })
                        : Ok(new { id = signingData.Id, hashToSign = signingData.HashToSign, contentStamp = stampPlacement });
                }
                return Ok(new
                {
                    id = signingData.Id,
                    hashToSign = signingData.HashToSign,
                    preparation = PreparationBody(prepared.Report, preparationCredits),
                    contentStamp = stampPlacement,
                });
            }
            catch (ApiValidationException ex) when (StampRefusalCodes.Contains(ex.Code))
            {
                // Planned before anything is written: nothing to clean up (portál #511).
                Logger.LogInformation("Presign refused: {Code} ({Detail})", ex.Code, ex.Message);
                return StampRefused(ex);
            }
            catch (ApiValidationException ex) when (PreparationRefusalCodes.Contains(ex.Code))
            {
                // Thrown before the file is written: nothing to clean up.
                Logger.LogInformation("Presign refused: {Code} ({Detail})", ex.Code, ex.Message);
                return Conflict(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
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

        /// <summary>The stamp or the seal beside it has no place, or the certification forbids it (422, portál #511).</summary>
        internal static readonly IReadOnlySet<string> StampRefusalCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            SealPlacement.NoRoomCode, PdfSigningService.CertifiedNoChangesCode,
        };

        private IActionResult StampRefused(ApiValidationException ex) =>
            UnprocessableEntity(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });

        /// <summary>
        /// Where presign would put the content stamp and the seal beside it, without writing anything
        /// (portál #511): the portal asks before it reserves credits or spends an SMS. Same input and
        /// the same 422 refusals as <c>/api/presign</c>; free.
        /// </summary>
        [CapacityPool(CapacityPoolKind.Heavy)]
        [HttpPost("/api/presign/placement")]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<IActionResult> PresignPlacement([FromBody] PreSignInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;
            try
            {
                var pdf = GuardedPdfFromBase64(input.PdfContent, "Presign", checkActiveContent: !input.FlattenForm);
                input.PdfContent = string.Empty;
                return Ok(new { contentStamp = PdfSigningService.PlanContentStamp(input, pdf).Placement });
            }
            catch (ApiValidationException ex) when (StampRefusalCodes.Contains(ex.Code))
            {
                return StampRefused(ex);
            }
            catch (ApiValidationException ex)
            {
                return InputRefused(ex);
            }
        }

        /// <summary>The document as it is cannot be prepared or signed as asked (409): retrying the same request will not help.</summary>
        internal static readonly IReadOnlySet<string> PreparationRefusalCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            PdfFormFlattener.AlreadySignedCode, PdfFormFlattener.XfaDynamicCode, PdfFormFlattener.IncompleteCode, PdfFormFlattener.FormInvalidCode,
            DocumentPreparation.ConversionFailedCode, DocumentPreparation.NotPreservedCode, DocumentPreparation.EmbeddedFilesCode,
        };

        /// <summary>What the preparation did, in the answer of presign, timestamp and visual-sign (#184).</summary>
        private static object PreparationBody(DocumentPreparation.Report report, int credits) => new
        {
            flatten = report.Flatten,
            flattenedFields = report.FlattenedFields,
            removed = report.Removed,
            pdfa = report.Pdfa,
            conformance = report.Conformance,
            credits,
        };

        /// <summary>
        /// Flattening and PDF/A conversion before the signature (#184), one step for presign, timestamp
        /// and visual-sign, under the document deadline and the operator's switches of the two
        /// features. Priced like <c>/api/flatten-form</c> and <c>/api/convert/pdfa</c> for every caller.
        /// Presign is charged by <c>/api/sign</c> and so checks the balance here
        /// (<paramref name="checkCredits"/>); timestamp and visual-sign add the credits to their own
        /// reservation, which answers 402 itself.
        /// </summary>
        private async Task<(DocumentPreparation.Result? Result, int Credits, IActionResult? Refused)> PrepareAsync(
            User user, IDocumentPreparationRequest input, byte[] pdf, string context, string errorKey, bool checkCredits)
        {
            foreach (var (asked, feature) in new[] { (input.ConvertToPdfa, "pdfa"), (input.FlattenForm, "flatten-form") })
            {
                if (asked && await DotNetSigningServer.Middleware.ApiGateMiddleware.IsFeatureOffAsync(HttpContext.RequestServices, feature))
                {
                    Response.Headers.RetryAfter = "60";
                    return (null, 0, StatusCode(StatusCodes.Status503ServiceUnavailable,
                        new { code = "FEATURE_DISABLED", message = Localizer["Error_FEATURE_DISABLED"].Value }));
                }
            }

            var guard = SafetyGuard;
            var limits = LimitGuard.Limits;
            var conversion = HttpContext.RequestServices.GetRequiredService<PdfConversionService>();
            try
            {
                if (input.ConvertToPdfa) DocumentPreparation.ResolveConformance(input.PdfaConformance);
                var (result, refused) = await RunWithDeadlineAsync(user.Id, input.ConvertToPdfa ? "pdfa" : "flatten-form",
                    _ => Task.FromResult(DocumentPreparation.Prepare(pdf, input, limits, guard, conversion, context)));
                if (refused != null) return (null, 0, refused);

                var report = result!.Report;
                Logger.LogInformation("{Context} preparation: flatten {Flatten} ({Fields} fields, removed [{Removed}]), PDF/A {Pdfa} {Conformance}",
                    context, report.Flatten ?? "-", report.FlattenedFields, string.Join(",", report.Removed), report.Pdfa ?? "-", report.Conformance ?? "-");

                var credits = 0;
                if (report.Flatten is DocumentPreparation.FlattenOutcome.Flattened or DocumentPreparation.FlattenOutcome.Unchanged)
                    credits += Prices.Credits(Services.Pricing.PricedOperations.FlattenForm);
                if (report.Pdfa == DocumentPreparation.PdfaOutcome.Converted)
                    credits += Prices.Credits(Services.Pricing.PricedOperations.PdfaConvert, CalculateCreditsForPages(result.Pages));
                // The signature pays for it: a presign that /api/sign could never charge is refused now.
                var signCredits = Prices.Credits(Services.Pricing.PricedOperations.Sign);
                if (checkCredits && LacksCredits(user, signCredits + credits))
                {
                    Logger.LogWarning(Logging.LoggingEvents.CreditsInsufficient, "Credits insufficient for {Context} preparation of user {UserId}", context, user.Id);
                    return (null, 0, StatusCode(StatusCodes.Status402PaymentRequired, new
                    {
                        code = ApiOperationService.InsufficientCreditsCode,
                        message = Localizer["NotEnoughCredits", signCredits + credits].Value,
                    }));
                }
                return (result, credits, null);
            }
            catch (ApiValidationException ex) when (PreparationRefusalCodes.Contains(ex.Code))
            {
                Logger.LogInformation("{Context} preparation refused: {Code} ({Detail})", context, ex.Code, ex.Message);
                return (null, 0, Conflict(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value }));
            }
            catch (ApiValidationException ex)
            {
                return (null, 0, InputRefused(ex));
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "{Context} preparation failed", context);
                return (null, 0, SafeProblem(Localizer[errorKey], ex));
            }
        }

        /// <summary>
        /// Preparation for a paid operation that runs it before the reservation (timestamp,
        /// visual-sign). A completed operation under the same <c>Idempotency-Key</c> is answered from
        /// its stored result without new work, so its preparation is not run again either: a retry
        /// after a lost answer must not be refused for the credits the first call already took.
        /// </summary>
        private async Task<(DocumentPreparation.Result? Result, int Credits, IActionResult? Refused)> PrepareForOperationAsync(
            User user, IDocumentPreparationRequest input, Func<byte[]> pdf, string context, string errorKey)
        {
            if (!DocumentPreparation.Requested(input)) return (null, 0, null);
            if (Request.Headers.TryGetValue(IdempotencyKeyHeader, out var key)
                && ApiOperationService.IsValidOperationId(key.ToString())
                && await Operations.FindAsync(user.Id, key.ToString(), HttpContext.RequestAborted) is { State: ApiOperationState.Completed })
            {
                return (null, 0, null);
            }
            return await PrepareAsync(user, input, pdf(), context, errorKey, checkCredits: false);
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
            // Another account's presign or signature is answered as missing (#131): Forbid() redirected
            // an API caller to the sign-in page, and a 403 would confirm that the id exists.
            if (existing == null)
            {
                if (presign == null || presign.UserId != user.Id)
                {
                    return NotFound(new { code = SigningDataNotFoundCode, message = Localizer["SigningDataNotFound"].Value });
                }
            }
            else if (existing.UserId != user.Id)
            {
                Logger.LogWarning(Logging.LoggingEvents.AuthFailed, "Sign refused for user {UserId}: presign {PresignId} belongs to another account", user.Id, input.Id);
                return NotFound(new { code = SigningDataNotFoundCode, message = Localizer["SigningDataNotFound"].Value });
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
                    // The presign's flattening and PDF/A conversion are paid with the signature (#184).
                    baseCost: Prices.Credits(Services.Pricing.PricedOperations.Sign) + (presign?.PreparationCredits ?? 0),
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
                return await RunOperationAsync(user, "sign-pfx", fingerprint, baseCost: Prices.Credits(Services.Pricing.PricedOperations.SignPfx), async _ =>
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

            byte[]? pdf = null;
            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Timestamp");
                if (DocumentPreparation.Requested(input))
                {
                    // Active content is what flattening removes; the prepared document gets the full check.
                    pdf = GuardedPdfFromBase64(input.PdfContent, "Timestamp", checkActiveContent: !input.FlattenForm);
                }
                else
                {
                    SafetyGuard.EnsureSafePdf(input.PdfContent, "Timestamp");
                }
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            return await TimestampCoreAsync(user, input, pdf, ApiOperationService.Fingerprint("timestamp", input));
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
                SafetyGuard.EnsureSafePdf(pdf!, "Timestamp", checkActiveContent: !input!.FlattenForm);
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
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
            // Flattening and PDF/A before the timestamp (#184), the step of presign.
            var (prepared, preparationCredits, refused) = await PrepareForOperationAsync(
                user, input, () => pdf ?? Convert.FromBase64String(input.PdfContent), "Timestamp", "TimestampError");
            if (refused != null) return refused;
            try
            {
                return await RunOperationAsync(user, "timestamp", fingerprint, baseCost: Prices.Credits(Services.Pricing.PricedOperations.Timestamp) + preparationCredits, async _ =>
                {
                    // Never the unconverted document in place of the prepared one (only a replay skips preparation, and it never runs this).
                    if (prepared == null && DocumentPreparation.Requested(input)) throw new InvalidOperationException("Document preparation did not run.");
                    if (input.TemplateId.HasValue)
                    {
                        var signatureField = await GetSignatureFieldAsync(input.TemplateId.Value, user.Id);
                        input.SignRect = signatureField.Rect;
                        input.SignPageNumber = signatureField.Page <= 0 ? 1 : signatureField.Page;
                        input.FieldName = string.IsNullOrWhiteSpace(signatureField.FieldName)
                            ? input.FieldName
                            : signatureField.FieldName;
                    }
                    if (prepared != null)
                    {
                        var stamped = _signingService.ApplyDocumentTimestamp(input, prepared.Pdf, prepared.KeepPdfA);
                        return new PaidResult(new { result = stamped, preparation = PreparationBody(prepared.Report, preparationCredits) }, stamped);
                    }
                    var result = pdf == null ? _signingService.ApplyDocumentTimestamp(input) : _signingService.ApplyDocumentTimestamp(input, pdf);
                    return new PaidResult(new { result }, result);
                });
            }
            catch (ApiValidationException ex) when (PreparationRefusalCodes.Contains(ex.Code))
            {
                Logger.LogInformation("Timestamp refused: {Code} ({Detail})", ex.Code, ex.Message);
                return Conflict(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
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
                return await RunOperationAsync(user, "extend-signature", fingerprint, baseCost: Prices.Credits(Services.Pricing.PricedOperations.ExtendSignature), _ =>
                {
                    var pdfBytes = pdf ?? Convert.FromBase64String(input.PdfContent);
                    var required = string.IsNullOrWhiteSpace(input.RequireCompleteRevocationFor) ? null : input.RequireCompleteRevocationFor;
                    var extended = Convert.ToBase64String(_ltvService.Extend(pdfBytes, tsaClient, input.AddArchiveTimestamp, required));
                    return Task.FromResult(new PaidResult(new { result = extended }, extended));
                });
            }
            catch (FormatException)
            {
                return BadRequest(new { message = Localizer["InvalidBase64"].Value });
            }
            catch (ApiValidationException ex)
            {
                // The detail names the certificates without revocation data; the answer carries only the code.
                Logger.LogWarning("Extend signature refused: {Code} ({Detail})", ex.Code, ex.Message);
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
            // The provisioning token may ask too (#131); it has no account of its own to load here.
            var provisioning = User?.Identity?.AuthenticationType == ApiKeyAuthenticationHandler.SchemeName
                               && SessionClaims.IsProvisioningToken(User);
            if (!provisioning)
            {
                var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
                if (error != null || user == null) return error!;
            }

            // No platform seal exists; the field only reports that.
            // AI: switched on, a usable provider and a price for data extraction (never offered free).
            // `enabled` keeps meaning data extraction; `detectFields` adds field detection (its own prices).
            var gateway = HttpContext.RequestServices.GetService<Services.Ai.AiGateway>();
            var aiOn = gateway != null && await gateway.IsOfferedAsync(Prices, PricedOperations.AiExtract, PricedOperations.AiExtractPages, HttpContext.RequestAborted);
            var detectOn = gateway != null && await gateway.IsOfferedAsync(Prices, PricedOperations.AiDetectFields, PricedOperations.AiDetectFieldsPages, HttpContext.RequestAborted);
            return Ok(new ServerCapabilities
            {
                Seal = new SealCapability { Enabled = false },
                Ai = new AiCapability { Enabled = aiOn, DetectFields = detectOn },
            });
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

            byte[]? pdf = null;
            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Visual sign");
                if (DocumentPreparation.Requested(input))
                {
                    // Active content is what flattening removes; the prepared document gets the full check.
                    pdf = GuardedPdfFromBase64(input.PdfContent, "Visual sign", checkActiveContent: !input.FlattenForm);
                }
                else
                {
                    SafetyGuard.EnsureSafePdf(input.PdfContent, "Visual sign");
                }
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
            }
            catch (ApiValidationException ex)
            {
                Logger.LogWarning("[visual-sign] Limit check failed: {Code}", ex.Code);
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            return await VisualSignCoreAsync(user, input, pdf, ApiOperationService.Fingerprint("visual-sign", input));
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
                SafetyGuard.EnsureSafePdf(pdf!, "Visual sign", checkActiveContent: !input!.FlattenForm);
                LimitGuard.EnsureImageWithinLimit(input.SignImageContent, "Signature image");
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
            // Flattening and PDF/A before the visual signature (#184), the step of presign.
            var (prepared, preparationCredits, refused) = await PrepareForOperationAsync(
                user, input, () => pdf ?? Convert.FromBase64String(input.PdfContent), "Visual sign", "VisualSignError");
            if (refused != null) return refused;
            try
            {
                return await RunOperationAsync(user, "visual-sign", fingerprint, baseCost: Prices.Credits(Services.Pricing.PricedOperations.VisualSign) + preparationCredits, async _ =>
                {
                    // Never the unconverted document in place of the prepared one (only a replay skips preparation, and it never runs this).
                    if (prepared == null && DocumentPreparation.Requested(input)) throw new InvalidOperationException("Document preparation did not run.");
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
                    if (prepared != null)
                    {
                        var drawn = _signingService.ApplyVisualSign(input, prepared.Pdf, prepared.KeepPdfA);
                        return new PaidResult(new { result = drawn, preparation = PreparationBody(prepared.Report, preparationCredits) }, drawn);
                    }
                    var result = pdf == null ? _signingService.ApplyVisualSign(input) : _signingService.ApplyVisualSign(input, pdf);
                    Logger.LogInformation("[visual-sign] Success, result length={Len}", result?.Length ?? 0);
                    return new PaidResult(new { result }, result);
                });
            }
            catch (ApiValidationException ex) when (PreparationRefusalCodes.Contains(ex.Code))
            {
                Logger.LogInformation("[visual-sign] Refused: {Code} ({Detail})", ex.Code, ex.Message);
                return Conflict(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
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

            if (!user.EffectiveSealAllowed)
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

            try
            {
                LimitGuard.EnsurePdfWithinLimit(input.PdfContent, "Attachment PDF");
                SafetyGuard.EnsureSafePdf(input.PdfContent, "Attachment PDF");
                LimitGuard.EnsureAttachmentWithinLimit(input.AttachmentContent, "Attachment");
                EmbeddedFiles.ParseRelationship(input.AfRelationship);
            }
            catch (ApiValidationException ex)
            {
                return BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }

            return await AttachmentCoreAsync(user, input, null, ApiOperationService.Fingerprint("attachment", input));
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

            var (input, pdf, refused) = await ReadBinaryPdfAsync<AddAttachmentInput>(
                "Attachment PDF", i => i.PdfContent, extraMetadataBytes: LimitGuard.Limits.AttachmentMaxBytes / 3 * 4 + 1024);
            if (refused != null) return refused;
            try
            {
                SafetyGuard.EnsureSafePdf(pdf!, "Attachment PDF");
                LimitGuard.EnsureAttachmentWithinLimit(input!.AttachmentContent, "Attachment");
                EmbeddedFiles.ParseRelationship(input.AfRelationship);
            }
            catch (ApiValidationException ex)
            {
                return InputRefused(ex);
            }

            return await AttachmentCoreAsync(user, input, pdf, BinaryFingerprint("attachment", input, pdf!));
        }

        /// <summary>attachment po vstupní kontrole, společné oběma kanálům; <c>pdf == null</c> = dokument je v <c>input.PdfContent</c>.</summary>
        private async Task<IActionResult> AttachmentCoreAsync(User user, AddAttachmentInput input, byte[]? pdf, string fingerprint)
        {
            // Bez certifikátu se odmítá dřív, než vznikne operace a rezervace (#513).
            if (_signingService.MissingEvidenceCertificate(input))
            {
                return EvidenceCertNotConfigured();
            }

            try
            {
                return await RunOperationAsync(
                    user,
                    "attachment",
                    fingerprint,
                    baseCost: Prices.Credits(Services.Pricing.PricedOperations.Attachment),
                    _ =>
                    {
                        var result = pdf == null ? _signingService.AddAttachment(input) : _signingService.AddAttachment(input, pdf);
                        return Task.FromResult(new PaidResult(new { result }, result));
                    });
            }
            catch (ApiValidationException ex) when (ex.Code == PdfSigningService.EvidenceCertNotConfiguredCode)
            {
                return EvidenceCertNotConfigured();
            }
            catch (ApiValidationException ex) when (ex.Code == PdfFormFlattener.AlreadySignedCode)
            {
                // Refusal of the document as it is, like flatten-form: 409, nothing charged.
                return Conflict(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });
            }
            catch (ApiValidationException ex)
            {
                return InputRefused(ex);
            }
            catch (Exception ex)
            {
                Logger.LogError(Logging.LoggingEvents.ApiError, ex, "Add attachment failed");
                return SafeProblem(Localizer["AttachmentError"], ex);
            }
        }

        /// <summary>503: chyba nasazení, ne vstupu; volající nemá co opravit v požadavku.</summary>
        private IActionResult EvidenceCertNotConfigured()
        {
            Logger.LogError(
                Logging.LoggingEvents.ApiError,
                "Evidence attachment refused: no encryption certificate in the request and EVIDENCE_ENCRYPTION_CERTIFICATE_PEM(_BASE64) is not set");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    code = PdfSigningService.EvidenceCertNotConfiguredCode,
                    message = Localizer[$"Error_{PdfSigningService.EvidenceCertNotConfiguredCode}"].Value,
                });
        }


    }
}
