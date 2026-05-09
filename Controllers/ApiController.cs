using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using iText.Kernel.Pdf;

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

        protected async Task<User?> GetAuthenticatedUserAsync(string? originHeader = null)
        {
            // Prefer cookie-authenticated user
            if (User?.Identity?.IsAuthenticated == true)
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

            // Fallback to API token
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

            // Enterprise users bypass the credits check (billed manually based on tracked usage)
            if (requiredCredits > 0 && !user.IsEnterprise && user.CreditsRemaining < requiredCredits)
            {
                Logger.LogWarning(Logging.LoggingEvents.CreditsInsufficient, "Credits insufficient for user {UserId}", user.Id);
                return (null, StatusCode(StatusCodes.Status402PaymentRequired, new { message = Localizer["NoCreditsRemaining"].Value }));
            }

            return (user, null);
        }

        protected async Task<bool> DebitUserAsync(User user, int debit = 1, string? operation = null, Guid? documentId = null)
        {
            if (debit <= 0)
            {
                return true;
            }

            // Tier is computed by UserConcurrencyMiddleware at semaphore acquisition (atomic with
            // WaitAsync success). Reading it from HttpContext.Items avoids the SemaphoreSlim.CurrentCount
            // race where multiple requests can observe the same drifting inFlight value near tier boundaries.
            var tier = HttpContext?.Items[DotNetSigningServer.Middleware.UserConcurrencyMiddleware.TierItemKey] is int t ? t : 1;
            var effectiveDebit = debit * tier;

            // Enterprise users: skip the credits decrement but still record usage for billing
            if (!user.IsEnterprise)
            {
                var rowsAffected = await DbContext.Database.ExecuteSqlRawAsync(
                    "UPDATE \"Users\" SET \"CreditsRemaining\" = \"CreditsRemaining\" - {0} WHERE \"Id\" = {1} AND \"CreditsRemaining\" >= {2}",
                    effectiveDebit, user.Id, effectiveDebit);

                if (rowsAffected == 0)
                {
                    return false;
                }

                // Refresh the in-memory entity to reflect the new value
                await DbContext.Entry(user).ReloadAsync();
            }

            // Track usage for reporting (base cost, tier, effective debit)
            try
            {
                DbContext.UsageRecords.Add(new UsageRecord
                {
                    UserId = user.Id,
                    DocumentId = documentId,
                    Count = effectiveDebit,
                    BaseCost = debit,
                    Tier = tier,
                    Operation = operation,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await DbContext.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Usage tracking is best-effort — never fail the request because of it
                Logger.LogWarning(ex, "Failed to write UsageRecord for user {UserId}", user.Id);
            }

            // Check auto-recharge threshold (fire-and-forget to not block the API response)
            // Skip for Enterprise users — they're billed manually
            if (!user.IsEnterprise && user.AutoRechargeEnabled && user.AutoRechargeQuantity > 0)
            {
                if (user.CreditsRemaining < AutoRechargeService.ThresholdCredits)
                {
                    var userId = user.Id;
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
            }

            return true;
        }

        protected async Task<PdfFieldDefinition> GetSignatureFieldAsync(Guid templateId, Guid userId)
        {
            var template = await PdfTemplateService.GetTemplateAsync(templateId, userId);
            var signatureField = template.Fields.FirstOrDefault(f => f.Type == PdfFieldType.Signature);
            if (signatureField == null)
            {
                throw new InvalidOperationException("Template does not contain a signature field.");
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

        protected IActionResult PaymentRequired(User user, int requiredCredits)
        {
            Logger.LogWarning(Logging.LoggingEvents.CreditsInsufficient, "Credits insufficient for user {UserId}", user.Id);
            return StatusCode(StatusCodes.Status402PaymentRequired, new { message = Localizer["NotEnoughCredits", requiredCredits].Value });
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
