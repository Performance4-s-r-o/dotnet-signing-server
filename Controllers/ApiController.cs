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
            return dbUser;
        }

        protected async Task<(User? user, ActionResult? error)> EnsureUserWithCreditsAsync(int requiredCredits = 1, string? originHeader = null)
        {
            var user = await GetAuthenticatedUserAsync(originHeader);
            if (user == null)
            {
                Logger.LogWarning(Logging.LoggingEvents.AuthFailed, "API auth failed");
                return (null, Unauthorized());
            }

            if (requiredCredits > 0 && user.CreditsRemaining < requiredCredits)
            {
                Logger.LogWarning(Logging.LoggingEvents.CreditsInsufficient, "Credits insufficient for user {UserId}", user.Id);
                return (null, StatusCode(StatusCodes.Status402PaymentRequired, new { message = Localizer["NoCreditsRemaining"].Value }));
            }

            return (user, null);
        }

        protected async Task<bool> DebitUserAsync(User user, int debit = 1)
        {
            if (debit <= 0)
            {
                return true;
            }

            var rowsAffected = await DbContext.Database.ExecuteSqlRawAsync(
                "UPDATE \"Users\" SET \"CreditsRemaining\" = \"CreditsRemaining\" - {0} WHERE \"Id\" = {1} AND \"CreditsRemaining\" >= {2}",
                debit, user.Id, debit);

            if (rowsAffected == 0)
            {
                return false;
            }

            // Refresh the in-memory entity to reflect the new value
            await DbContext.Entry(user).ReloadAsync();
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
