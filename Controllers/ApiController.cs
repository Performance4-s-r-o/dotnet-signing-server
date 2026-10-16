using DotNetSigningServer.Data;
using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Operations;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using iText.Kernel.Pdf;
using System.Text.Json;

namespace DotNetSigningServer.Controllers
{
    /// <summary>
    /// Shared base class for all API controllers.
    /// Provides authentication, credit management, and common helpers.
    /// </summary>
    [ApiController]
    [IgnoreAntiforgeryToken]
    public abstract class ApiControllerBase : ControllerBase
    {
        protected readonly ApplicationDbContext DbContext;
        protected readonly IApiAuthService ApiAuthService;
        protected readonly ILogger Logger;
        protected readonly ContentLimitGuard LimitGuard;
        protected readonly BillingOptions BillingOptions;
        protected readonly IWebHostEnvironment Env;
        protected readonly PdfTemplateService PdfTemplateService;
        protected readonly IStringLocalizer<SharedStrings> Localizer;

        /// <summary>
        /// Opens an incoming PDF and refuses encrypted, oversized-by-pages or
        /// script-bearing files. Resolved from the request scope rather than
        /// the constructor so that every derived controller keeps its
        /// constructor untouched.
        /// </summary>
        protected PdfSafetyGuard SafetyGuard =>
            HttpContext.RequestServices.GetRequiredService<PdfSafetyGuard>();

        /// <summary>Operation state and reservations (#77, #82). Request-scoped, like <see cref="SafetyGuard"/>.</summary>
        protected ApiOperationService Operations =>
            HttpContext.RequestServices.GetRequiredService<ApiOperationService>();

        /// <summary>The caller's operation id; the portal sends its <c>operationId</c> here.</summary>
        public const string IdempotencyKeyHeader = "Idempotency-Key";
        public const string OperationIdHeader = "X-Operation-Id";
        public const string OperationStateHeader = "X-Operation-State";
        public const string ReplayedHeader = "Idempotent-Replayed";

        protected ApiControllerBase(
            ApplicationDbContext dbContext,
            IApiAuthService apiAuthService,
            ILogger logger,
            ContentLimitGuard limitGuard,
            IOptions<BillingOptions> billingOptions,
            IWebHostEnvironment env,
            PdfTemplateService pdfTemplateService,
            IStringLocalizer<SharedStrings> localizer)
        {
            DbContext = dbContext;
            ApiAuthService = apiAuthService;
            Logger = logger;
            LimitGuard = limitGuard;
            BillingOptions = billingOptions.Value;
            Env = env;
            PdfTemplateService = pdfTemplateService;
            Localizer = localizer;
        }

        /// <summary>
        /// Returns a Problem response with trace ID. Only includes exception details in Development.
        /// </summary>
        protected ObjectResult SafeProblem(string genericMessage, Exception? ex = null)
        {
            var traceId = HttpContext.TraceIdentifier;
            var detail = Env.IsDevelopment() && ex != null
                ? $"{genericMessage}: {ex.Message}"
                : $"{genericMessage} (traceId: {traceId})";

            return Problem(detail: detail, statusCode: StatusCodes.Status500InternalServerError);
        }

        /// <summary>Odmítnutý vstup: 400 s kódem a přeloženou zprávou, jak to dělají všechny endpointy.</summary>
        protected IActionResult InputRefused(ApiValidationException ex) =>
            BadRequest(new { code = ex.Code, message = Localizer[$"Error_{ex.Code}"].Value });

        /// <summary>
        /// PDF z JSON kanálu (base64), zkontrolované stejně jako dosud — limit velikosti ještě
        /// před dekódováním, pak <see cref="PdfSafetyGuard"/> — ale dekódované JEDNOU: bajty jdou
        /// dál do práce, místo aby je každý krok znovu dekódoval z řetězce. Stejné kódy chyb jako
        /// <see cref="PdfSafetyGuard.EnsureSafePdf(string?, string, bool)"/>.
        /// </summary>
        protected byte[] GuardedPdfFromBase64(string? base64, string context, bool checkActiveContent = true)
        {
            LimitGuard.EnsurePdfWithinLimit(base64 ?? string.Empty, context);
            if (string.IsNullOrWhiteSpace(base64))
            {
                throw new ApiValidationException("PDF_INVALID", $"{context}: no content");
            }
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                throw new ApiValidationException("PDF_INVALID", $"{context}: not base64");
            }
            SafetyGuard.EnsureSafePdf(bytes, context, checkActiveContent);
            return bytes;
        }

        /// <summary>
        /// Tělo binárního kanálu (#74): metadata a PDF jako bajty. Čte se až po ověření volajícího
        /// (JSON kanál své tělo načte už při bindingu, tady je to přísnější, ne volnější). Limit
        /// velikosti PDF se hlídá při čtení; <see cref="PdfSafetyGuard"/> volá endpoint sám, stejně
        /// jako v JSON kanálu.
        /// </summary>
        /// <remarks>
        /// <b>Akce binárního kanálu nesmí sahat na <c>Request.Form</c> / <c>ReadFormAsync</c></b>
        /// (ani přes <c>[FromForm]</c>/<c>IFormFile</c>): ASP.NET by celé tělo načetl a nabufferoval
        /// (nad 64 KB na disk) bez limitu části, dřív než by kterákoli kontrola proběhla. Tělo čte
        /// jen tato metoda, proudem a s limitem každé části.
        /// </remarks>
        /// <param name="inlinePdf">Base64 PDF v metadatech: v binárním kanálu nesmí být, jinak by
        /// nebylo jasné, který dokument platí.</param>
        /// <summary>
        /// Strop části <c>metadata</c>: až čtyři obrázky v base64 (podpis, razítko, logo, pozadí —
        /// tolik jich nese presign i pečeť) po <see cref="LimitsOptions.ImageMaxBytes"/> a 1 MB
        /// rezerva na zbytek JSONu. Výchozí limity → zhruba 6,3 MB.
        /// </summary>
        protected long MetadataMaxBytes => LimitGuard.Limits.ImageMaxBytes * 4 / 3 * 4 + 1024 * 1024;

        protected async Task<(T? Metadata, byte[]? Pdf, IActionResult? Error)> ReadBinaryPdfAsync<T>(
            string context, Func<T, string?> inlinePdf) where T : class
        {
            var json = HttpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
            try
            {
                var (metadata, pdf) = await BinaryPdfChannel.ReadAsync<T>(
                    Request.ContentType, Request.Body, Request.ContentLength, json,
                    LimitGuard.Limits.PdfMaxBytes, MetadataMaxBytes, HttpContext.RequestAborted,
                    maxBodyBytes: LimitGuard.Limits.RequestBodyLimitBytes);
                if (!string.IsNullOrEmpty(inlinePdf(metadata)))
                {
                    throw new ApiValidationException(BinaryPdfChannel.InvalidCode, "pdfContent in metadata");
                }
                return (metadata, pdf, null);
            }
            catch (ApiValidationException ex)
            {
                Logger.LogInformation("{Context}: binary request refused ({Code}): {Detail}", context, ex.Code, ex.Message);
                return (null, null, InputRefused(ex));
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return (null, null, StatusCode(StatusCodes.Status413PayloadTooLarge, new { message = Localizer["RequestExceededLimit"].Value }));
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex)
            {
                Logger.LogInformation(ex, "{Context}: binary request body unreadable", context);
                return (null, null, InputRefused(new ApiValidationException(BinaryPdfChannel.InvalidCode)));
            }
        }

        protected async Task<User?> GetAuthenticatedUserAsync(string? originHeader = null)
        {
            // Prefer the principal the authentication middleware established. A cookie
            // session counts only on actions the web UI calls from the browser
            // ([AllowCookieSession]); everywhere else /api is API key only (#76).
            if (User?.Identity?.IsAuthenticated == true
                && (User.Identity.AuthenticationType == ApiKeyAuthenticationHandler.SchemeName
                    || HttpContext.GetEndpoint()?.Metadata.GetMetadata<AllowCookieSessionAttribute>() != null))
            {
                var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                                  ?? User.FindFirst("sub")?.Value;
                if (Guid.TryParse(userIdClaim, out var guid))
                {
                    var cookieUser = await DbContext.Users.FirstOrDefaultAsync(u => u.Id == guid);
                    if (cookieUser != null)
                    {
                        if (!cookieUser.IsActive) return null;
                        TagAuthenticatedUser(cookieUser.Id);
                        return cookieUser;
                    }
                }
            }

            // Fallback to API token. Same boundary as SmartAuth: a bearer token is an API
            // credential only under /api, whatever route a future action declares (#76).
            if (!AuthenticationSchemeSelection.IsApiPath(Request.Path))
            {
                return null;
            }
            Logger.LogDebug("Validating API token from Authorization header");
            var tokenUser = await ApiAuthService.ValidateTokenAsync(Request.Headers["Authorization"].ToString(), originHeader, HttpContext.Connection.RemoteIpAddress);
            if (tokenUser == null)
            {
                return null;
            }

            var dbUser = await DbContext.Users.FirstOrDefaultAsync(u => u.Id == tokenUser.Id);
            if (dbUser != null && !dbUser.IsActive) return null;
            if (dbUser != null) TagAuthenticatedUser(dbUser.Id);
            return dbUser;
        }

        private void TagAuthenticatedUser(Guid userId)
        {
            if (HttpContext != null)
            {
                HttpContext.Items[DotNetSigningServer.Middleware.ApiRequestLoggingMiddleware.AuthenticatedUserIdItemKey] = userId;
            }
        }

        protected async Task<(User? user, ActionResult? error)> EnsureUserWithCreditsAsync(int requiredCredits = 1, string? originHeader = null)
        {
            var user = await GetAuthenticatedUserAsync(originHeader);
            if (user == null)
            {
                Logger.LogWarning(Logging.LoggingEvents.AuthFailed, "API auth failed");
                return (null, Unauthorized());
            }

            if (LacksCredits(user, requiredCredits))
            {
                Logger.LogWarning(Logging.LoggingEvents.CreditsInsufficient, "Credits insufficient for user {UserId}", user.Id);
                return (null, StatusCode(StatusCodes.Status402PaymentRequired, new { message = Localizer["NoCreditsRemaining"].Value }));
            }

            return (user, null);
        }

        /// <summary>What a paid operation produced.</summary>
        /// <param name="Body">The JSON body, exactly as answered (and replayed).</param>
        /// <param name="PdfBase64">Set when the body's <c>result</c> is a PDF, so <c>Accept: application/pdf</c> gets the bytes.</param>
        /// <param name="PdfHeaders">Headers that go with a raw PDF answer.</param>
        protected sealed record PaidResult(object Body, string? PdfBase64 = null, IReadOnlyDictionary<string, string>? PdfHeaders = null);

        /// <summary>
        /// Runs a paid operation under the state model of #77 with the reservation of #82:
        ///
        /// <list type="number">
        /// <item>finds or creates the operation for <c>Idempotency-Key</c> (or the presign) and
        /// reserves <paramref name="baseCost"/> × tier — 402 before any work when that fails;</item>
        /// <item>a completed operation is answered from the stored result, charged once;</item>
        /// <item>a failure returns the reservation and is rethrown for the endpoint to map as before,
        /// marked <c>failed_uncertain</c> when an external step (TSA, model) may have happened;</item>
        /// <item>success stores the result and charges in one transaction, then answers.</item>
        /// </list>
        ///
        /// Without a key the behaviour seen by the caller is the old one, except that the
        /// credits are taken before the work rather than after it.
        /// </summary>
        /// <param name="work">Gets a token that is the request's own only when nobody could fetch the
        /// result later: a keyed operation runs to the end even if the caller gave up, so the
        /// retry finds it.</param>
        /// <param name="afterCompleted">Best-effort clean-up once the result is stored (presign files).</param>
        protected async Task<IActionResult> RunOperationAsync(
            User user,
            string operation,
            string fingerprint,
            int baseCost,
            Func<CancellationToken, Task<PaidResult>> work,
            string? presignId = null,
            Func<Task>? afterCompleted = null)
        {
            string? clientKey = null;
            if (Request.Headers.TryGetValue(IdempotencyKeyHeader, out var keyValues))
            {
                clientKey = keyValues.ToString();
                if (!ApiOperationService.IsValidOperationId(clientKey))
                {
                    return BadRequest(new { code = "IDEMPOTENCY_KEY_INVALID", message = Localizer["Error_IDEMPOTENCY_KEY_INVALID"].Value });
                }
            }

            // Work that outlived its deadline still holds CPU and memory (iText cannot be
            // interrupted) after its slot was freed: refuse more until it has ended (#72).
            if (AbandonedWork.IsOverLimit(user.Id, Operations.Options.MaxAbandonedPerUser, Operations.Options.MaxAbandonedTotal, out var globalLimit))
            {
                Response.Headers.RetryAfter = "10";
                Logger.LogWarning("User {UserId} refused: {Count} abandoned operations still running ({Total} in the process)",
                    user.Id, AbandonedWork.For(user.Id), AbandonedWork.Total);
                return StatusCode(
                    globalLimit ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status429TooManyRequests,
                    new { message = Localizer[globalLimit ? "ServerAtCapacity" : "TooManyRequests"].Value, retryAfter = 10 });
            }

            var tier = HttpContext.Items[DotNetSigningServer.Middleware.UserConcurrencyMiddleware.TierItemKey] is int t ? t : 1;
            var begin = await Operations.BeginAsync(
                new OperationRequest(user.Id, user.IsEnterprise, operation, clientKey, fingerprint, baseCost, tier, presignId),
                HttpContext.RequestAborted);
            var op = begin.Operation;
            if (!string.IsNullOrEmpty(op.OperationId)) Response.Headers[OperationIdHeader] = op.OperationId;
            Response.Headers[OperationStateHeader] = op.State;

            switch (begin.Outcome)
            {
                case OperationStart.Replay:
                    return ReplayResult(op);
                case OperationStart.InProgress:
                    Response.Headers.RetryAfter = "5";
                    return Conflict(new { code = "OPERATION_IN_PROGRESS", message = Localizer["Error_OPERATION_IN_PROGRESS"].Value, operationId = op.OperationId, state = op.State });
                case OperationStart.KeyReused:
                    Response.Headers.Remove(OperationIdHeader);
                    Response.Headers.Remove(OperationStateHeader);
                    return UnprocessableEntity(new { code = "IDEMPOTENCY_KEY_REUSED", message = Localizer["Error_IDEMPOTENCY_KEY_REUSED"].Value });
                case OperationStart.ResultExpired:
                    return StatusCode(StatusCodes.Status410Gone, new { code = "OPERATION_RESULT_EXPIRED", message = Localizer["Error_OPERATION_RESULT_EXPIRED"].Value, operationId = op.OperationId, state = op.State });
                case OperationStart.InsufficientCredits:
                    Logger.LogWarning(Logging.LoggingEvents.CreditsInsufficient, "Reservation failed for user {UserId}", user.Id);
                    var price = baseCost * tier;
                    var message = price > 1 ? Localizer["NotEnoughCredits", price].Value : Localizer["NoCreditsRemaining"].Value;
                    return StatusCode(StatusCodes.Status402PaymentRequired, new { code = ApiOperationService.InsufficientCreditsCode, message });
            }

            var attempt = begin.Attempt;

            // A browser token spends from its own budget too (#80). The reservation is given back
            // when this method is left any way other than through a completed operation.
            BrowserTokenBudget? budget = null;
            if (SessionClaims.IsBrowserToken(User) && SessionClaims.TokenIdOf(User) is Guid budgetId)
            {
                budget = await BrowserTokenBudget.TryReserveAsync(DbContext, budgetId, baseCost * tier, Logger);
                if (budget == null)
                {
                    await Operations.FailAsync(op, attempt, uncertain: false, BrowserTokenScope.CreditLimitErrorCode);
                    Response.Headers[OperationStateHeader] = ApiOperationState.Failed;
                    return StatusCode(StatusCodes.Status402PaymentRequired, new
                    {
                        code = BrowserTokenScope.CreditLimitErrorCode,
                        message = Localizer["Error_TOKEN_CREDIT_LIMIT"].Value,
                    });
                }
            }
            await using var budgetGuard = budget;

            // Whether anybody can come back for the result: the caller's key, or the presign.
            var retrievable = op.ClientKey || presignId != null;
            if (HttpContext.RequestAborted.IsCancellationRequested)
            {
                await Operations.FailAsync(op, attempt, uncertain: false, "CLIENT_ABORTED");
                return StatusCode(499);
            }

            PaidResult result;
            // The deadline of the operation (#72): under Cloudflare's 100 s and the portal's wait.
            // A keyed operation ignores the caller leaving; one nobody can ask about stops with it.
            // Not `using`: abandoned work keeps the token, so it is disposed when the work ends.
            var deadline = new OperationDeadline(
                Operations.Options.DeadlineFor(operation),
                retrievable ? CancellationToken.None : HttpContext.RequestAborted);
            var heartbeat = new LeaseHeartbeat(
                HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>(),
                op.Id, attempt, Operations.Options.HeartbeatInterval, Logger);
            try
            {
                result = await AwaitWithDeadlineAsync(deadline, user.Id, work);
            }
            catch (Exception ex)
            {
                await heartbeat.DisposeAsync();
                var uncertain = ex is OperationDeadlineExceededException late
                    ? late.ExternalCallStarted
                    : IsUncertainFailure(ex);
                try
                {
                    await Operations.FailAsync(op, attempt, uncertain, FailureCode(ex));
                }
                catch (Exception releaseError)
                {
                    // The lease expires and the sweep returns the credits.
                    Logger.LogError(releaseError, "Could not release operation {OperationId}", op.OperationId);
                }
                Response.Headers[OperationStateHeader] = uncertain ? ApiOperationState.FailedUncertain : ApiOperationState.Failed;
                if (FindTsaFailure(ex) is { } tsaFailure)
                {
                    // However iText wrapped it (or the call site forgot to): a TSA that could not
                    // be talked to is TSA_UNREACHABLE, not a 500 (#72).
                    Logger.LogWarning(tsaFailure, "Operation {OperationId} ({Operation}) failed: TSA communication error ({TsaUrl})",
                        op.OperationId, operation, tsaFailure.TsaUrl);
                    return TsaUnreachableResult(tsaFailure);
                }
                if (ex is OperationDeadlineExceededException)
                {
                    Logger.LogWarning("Operation {OperationId} ({Operation}) exceeded its deadline of {Seconds} s; state {State}",
                        op.OperationId, operation, deadline.Limit.TotalSeconds, Response.Headers[OperationStateHeader].ToString());
                    return StatusCode(StatusCodes.Status504GatewayTimeout, new
                    {
                        code = "OPERATION_DEADLINE_EXCEEDED",
                        message = Localizer["Error_OPERATION_DEADLINE_EXCEEDED"].Value,
                        operationId = op.OperationId,
                        state = Response.Headers[OperationStateHeader].ToString(),
                    });
                }
                throw;
            }
            await heartbeat.DisposeAsync();

            if (!retrievable && HttpContext.RequestAborted.IsCancellationRequested)
            {
                // Nobody is listening and nobody can ask again: do not charge for it.
                await Operations.FailAsync(op, attempt, uncertain: false, "CLIENT_ABORTED");
                return StatusCode(499);
            }

            // Kept for replay only when somebody can come back for it (the caller's key or the
            // presign), and only up to a size: a signed document is not parked in the database
            // for a caller that can never ask again, and a huge batch is delivered, not stored.
            StoredResult? stored = null;
            if (retrievable)
            {
                var jsonOptions = HttpContext.RequestServices
                    .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
                var json = JsonSerializer.SerializeToUtf8Bytes(result.Body, result.Body.GetType(), jsonOptions);
                // The presign path is one PDF, not a batch, and it is the only way back to a
                // signature already paid for (the presign goes once it completes): no size limit.
                if (presignId != null || json.LongLength <= Operations.Options.MaxStoredResultBytes)
                {
                    stored = new StoredResult(StatusCodes.Status200OK, json, result.PdfBase64 != null, result.PdfHeaders);
                }
                else
                {
                    Logger.LogInformation(
                        "Result of operation {OperationId} is {Bytes} bytes, over the replay limit; delivered without storing",
                        op.OperationId, json.LongLength);
                }
            }

            var (completed, kept) = await CompleteOperationAsync(op, attempt, stored);
            if (!completed)
            {
                // The lease ran out and the credits went back: this result is not ours to hand out.
                throw new InvalidOperationException($"Operation {op.OperationId} lost its lease before completion.");
            }
            budget?.Commit();

            Response.Headers[OperationStateHeader] = ApiOperationState.Completed;
            // Clean-up (the presign) only once the result is safely kept: otherwise a retry
            // would find neither the result nor the presign.
            if (afterCompleted != null && kept)
            {
                try { await afterCompleted(); }
                catch (Exception ex) { Logger.LogWarning(ex, "Post-completion clean-up failed for {OperationId}", op.OperationId); }
            }
            await TriggerAutoRechargeIfLowAsync(user);

            if (result.PdfBase64 != null)
            {
                return PdfOrJsonResult(result.PdfBase64, result.Body, resp =>
                {
                    foreach (var (name, value) in result.PdfHeaders ?? new Dictionary<string, string>()) resp.Headers[name] = value;
                });
            }
            return Ok(result.Body);
        }

        /// <summary>
        /// The answer for a TSA failure (#72, #82): a code and a generic message, the detail only
        /// in the log. The exception text carries whatever the connection said (addresses, ports,
        /// TLS errors) and a tenant-chosen URL made that a free probe of the network behind us.
        /// An open circuit breaker answers 503 with Retry-After, at once; the code stays the same.
        /// </summary>
        protected IActionResult TsaUnreachableResult(TsaCommunicationException ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is TsaCircuitOpenException open)
                {
                    Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(open.RetryAfter.TotalSeconds)).ToString();
                    return StatusCode(StatusCodes.Status503ServiceUnavailable,
                        new { code = "TSA_UNREACHABLE", message = Localizer["Error_TSA_UNREACHABLE"].Value, tsaUrl = ex.TsaUrl });
                }
            }
            return StatusCode(StatusCodes.Status502BadGateway,
                new { code = "TSA_UNREACHABLE", message = Localizer["Error_TSA_UNREACHABLE"].Value, tsaUrl = ex.TsaUrl });
        }

        /// <summary>
        /// Runs the work on the pool with the deadline ambient, and stops waiting for it when the
        /// deadline passes: iText cannot be interrupted, so the answer must not depend on it. The
        /// token is cancelled first and the work gets a short grace to unwind (it shares this
        /// request's database context), then the operation is settled whatever it is still doing.
        /// </summary>
        private static async Task<PaidResult> AwaitWithDeadlineAsync(
            OperationDeadline deadline, Guid userId, Func<CancellationToken, Task<PaidResult>> work)
        {
            var running = deadline.Run(work);
            var abandoned = false;
            try
            {
                using var timer = new CancellationTokenSource();
                var finished = await Task.WhenAny(running, Task.Delay(deadline.Limit, timer.Token));
                if (finished == running)
                {
                    timer.Cancel();
                    try { return await running; }
                    catch (OperationCanceledException) when (deadline.Expired)
                    {
                        throw new OperationDeadlineExceededException(deadline.Limit, deadline.ExternalCallStarted);
                    }
                }

                deadline.Cancel();
                using var grace = new CancellationTokenSource();
                await Task.WhenAny(running, Task.Delay(OperationDeadline.CancelGrace, grace.Token));
                grace.Cancel();
                // It finished while being cancelled: the result exists (and the TSA may have
                // issued and billed a token for it). Throwing it away would charge nothing and
                // invite a retry that stamps a second time.
                if (running.IsCompletedSuccessfully) return running.Result;

                if (!running.IsCompleted) abandoned = true;
                else _ = running.Exception; // ended badly during the grace; observed, the deadline is the answer
                throw new OperationDeadlineExceededException(deadline.Limit, deadline.ExternalCallStarted);
            }
            finally
            {
                if (abandoned) AbandonedWork.Track(userId, running, deadline.Dispose);
                else if (running.IsCompleted) deadline.Dispose();
                else _ = running.ContinueWith(_ => deadline.Dispose(), TaskScheduler.Default);
            }
        }

        /// <summary>
        /// Completes the operation. When keeping the result fails (size, memory, a timeout of the
        /// one big parameter) the work is still done and paid for: complete without it, and a
        /// retry gets 410. Only when even that cannot be recorded is the reservation returned —
        /// and then nothing is delivered either.
        /// </summary>
        /// <returns>Whether it completed, and whether the result was kept for replay.</returns>
        private async Task<(bool Completed, bool Kept)> CompleteOperationAsync(ApiOperation op, int attempt, StoredResult? stored)
        {
            if (stored != null)
            {
                try
                {
                    var done = await Operations.CompleteAsync(op, attempt, stored);
                    return (done, done);
                }
                catch (Exception storeError)
                {
                    Logger.LogWarning(storeError, "Could not store the result of operation {OperationId}; completing without it", op.OperationId);
                }
            }

            try
            {
                // After a failed store this is also how a commit that did go through is
                // recognised: the row is already completed by this attempt.
                return (await Operations.CompleteAsync(op, attempt, null), false);
            }
            catch
            {
                // Conditional on the attempt: a no-op if the commit did go through.
                try { await Operations.FailAsync(op, attempt, uncertain: true, "COMPLETE_FAILED"); }
                catch (Exception releaseError) { Logger.LogError(releaseError, "Could not release operation {OperationId}", op.OperationId); }
                throw;
            }
        }

        /// <summary>The first answer again, without doing the work or charging.</summary>
        private IActionResult ReplayResult(ApiOperation op)
        {
            StoredResult stored;
            try
            {
                stored = Operations.ReadResult(op);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                // A row the current key ring or purpose cannot open (an older build, a lost
                // key): the result is gone, the operation is still done and paid for.
                Logger.LogWarning(ex, "Stored result of operation {OperationId} cannot be decrypted", op.OperationId);
                return StatusCode(StatusCodes.Status410Gone, new { code = "OPERATION_RESULT_EXPIRED", message = Localizer["Error_OPERATION_RESULT_EXPIRED"].Value, operationId = op.OperationId, state = op.State });
            }
            Response.Headers[ReplayedHeader] = "true";
            if (stored.IsPdf && ClientPrefersPdf(Request.Headers["Accept"].ToString()))
            {
                using var doc = JsonDocument.Parse(stored.Json);
                if (doc.RootElement.TryGetProperty("result", out var pdf) && pdf.ValueKind == JsonValueKind.String)
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(pdf.GetString()!);
                        foreach (var (name, value) in stored.PdfHeaders ?? new Dictionary<string, string>()) Response.Headers[name] = value;
                        return File(bytes, "application/pdf");
                    }
                    catch (FormatException)
                    {
                        // Same fallback as PdfOrJsonResult: the JSON still carries it.
                    }
                }
            }
            return new ContentResult
            {
                Content = System.Text.Encoding.UTF8.GetString(stored.Json),
                ContentType = "application/json; charset=utf-8",
                StatusCode = stored.StatusCode,
            };
        }

        /// <summary>
        /// A failure after which an external effect may have happened: the TSA or the model was
        /// called and did not answer clearly. Nothing was delivered either way.
        /// </summary>
        internal static bool IsUncertainFailure(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is TsaCommunicationException or TimeoutException or HttpRequestException or OperationCanceledException
                    or OperationDeadlineExceededException { ExternalCallStarted: true })
                {
                    return true;
                }
            }
            return false;
        }

        private static TsaCommunicationException? FindTsaFailure(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is TsaCommunicationException tsa) return tsa;
            }
            return null;
        }

        private static string FailureCode(Exception ex) => ex switch
        {
            ApiValidationException validation => validation.Code,
            OperationDeadlineExceededException => "DEADLINE_EXCEEDED",
            _ when FindTsaFailure(ex) != null => "TSA_UNREACHABLE",
            FormatException => "INVALID_BASE64",
            _ when IsUncertainFailure(ex) => "UPSTREAM_UNCERTAIN",
            _ => "INTERNAL_ERROR",
        };

        /// <summary>Auto-recharge below the threshold, fire-and-forget so the answer is not held up.</summary>
        private async Task TriggerAutoRechargeIfLowAsync(User user)
        {
            // Skip for Enterprise users — they're billed manually
            if (user.IsEnterprise || !user.AutoRechargeEnabled || user.AutoRechargeQuantity <= 0) return;

            try
            {
                await DbContext.Entry(user).ReloadAsync();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Could not refresh the balance of user {UserId}", user.Id);
                return;
            }
            if (user.CreditsRemaining >= AutoRechargeService.ThresholdCredits) return;

            StartAutoRecharge(user.Id);
        }

        private void StartAutoRecharge(Guid userId)
        {
            var serviceScopeFactory = HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = serviceScopeFactory.CreateScope();
                    var rechargeService = scope.ServiceProvider.GetRequiredService<IAutoRechargeService>();
                    await rechargeService.TryAutoRechargeAsync(userId);
                }
                catch (Exception ex)
                {
                    using var scope = serviceScopeFactory.CreateScope();
                    scope.ServiceProvider.GetService<ILoggerFactory>()
                        ?.CreateLogger("AutoRecharge")
                        .LogError(ex, "Background auto-recharge failed for user {UserId}", userId);
                }
            });
        }

        protected async Task<PdfFieldDefinition> GetSignatureFieldAsync(Guid templateId, Guid userId)
        {
            var template = await PdfTemplateService.GetTemplateAsync(templateId, userId);
            var signatureField = template.Fields.FirstOrDefault(f => f.Type == PdfFieldType.Signature);
            if (signatureField == null)
            {
                throw new ApiValidationException("NO_SIGNATURE_FIELD");
            }
            return signatureField;
        }

        /// <summary>
        /// Returns a base64 PDF either as JSON (default) or as raw application/pdf bytes when
        /// the client explicitly sets Accept: application/pdf.
        /// </summary>
        /// <param name="base64Pdf">Base64-encoded PDF content.</param>
        /// <param name="jsonBody">Optional custom JSON body; defaults to { result = base64Pdf }.</param>
        /// <param name="onPdfResponse">Optional hook to set response headers when returning raw PDF.</param>
        protected IActionResult PdfOrJsonResult(string base64Pdf, object? jsonBody = null, Action<HttpResponse>? onPdfResponse = null)
        {
            if (ClientPrefersPdf(Request.Headers["Accept"].ToString()))
            {
                try
                {
                    var bytes = Convert.FromBase64String(base64Pdf);
                    onPdfResponse?.Invoke(Response);
                    return File(bytes, "application/pdf");
                }
                catch (FormatException)
                {
                    // Malformed base64 from the service — fall through to JSON so the client still sees the payload.
                }
            }
            return Ok(jsonBody ?? new { result = base64Pdf });
        }

        private static bool ClientPrefersPdf(string acceptHeader)
        {
            if (string.IsNullOrWhiteSpace(acceptHeader)) return false;
            foreach (var raw in acceptHeader.Split(','))
            {
                var type = raw.Trim();
                var sep = type.IndexOf(';');
                if (sep >= 0) type = type[..sep].Trim();
                if (string.Equals(type, "application/pdf", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Whether the caller must be refused for lack of credits.
        ///
        /// Enterprise users are exempt: they are billed manually from tracked
        /// usage, so CreditsRemaining is meaningless for them (see User.IsEnterprise
        /// and ApiOperationService, which skips the reservation but still writes a
        /// UsageRecord). The per-endpoint page-count pre-checks used to test
        /// CreditsRemaining directly and so refused exempt callers anyway — route
        /// every such check through here instead.
        /// </summary>
        protected static bool LacksCredits(User user, int requiredCredits)
        {
            return requiredCredits > 0 && !user.IsEnterprise && user.CreditsRemaining < requiredCredits;
        }

        protected static int CalculateCreditsForPages(int pageCount)
        {
            if (pageCount <= 0) return 0;
            return (int)Math.Ceiling(pageCount / 3.0);
        }

        protected static int CountPagesFromBase64(string pdfBase64)
        {
            using var ms = new MemoryStream(Convert.FromBase64String(pdfBase64));
            using var reader = new PdfReader(ms);
            using var pdf = new PdfDocument(reader);
            return pdf.GetNumberOfPages();
        }
    }
}
