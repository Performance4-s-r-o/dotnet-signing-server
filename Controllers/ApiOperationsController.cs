using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Controllers
{
    /// <summary>
    /// State of a paid operation by the caller's <c>Idempotency-Key</c> (#77).
    ///
    /// For reconciliation: a caller whose request timed out asks here whether the work was
    /// done and charged (<c>completed</c>) or not (<c>failed</c>, <c>failed_uncertain</c>), and
    /// fetches the result by repeating the request with the same key. 404 means the request
    /// never reached this server (or is older than the retention). Free.
    /// </summary>
    [Route("api")]
    public class ApiOperationsController : ApiControllerBase
    {
        public ApiOperationsController(
            ApplicationDbContext dbContext,
            IApiAuthService apiAuthService,
            ILogger<ApiOperationsController> logger,
            ContentLimitGuard limitGuard,
            IOptions<BillingOptions> billingOptions,
            IWebHostEnvironment env,
            PdfTemplateService pdfTemplateService,
            IStringLocalizer<SharedStrings> localizer)
            : base(dbContext, apiAuthService, logger, limitGuard, billingOptions, env, pdfTemplateService, localizer)
        {
        }

        [HttpGet("/api/operations/{operationId}")]
        public async Task<IActionResult> GetOperation(string operationId)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(requiredCredits: 0, originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (!ApiOperationService.IsValidOperationId(operationId))
            {
                return BadRequest(new { code = "IDEMPOTENCY_KEY_INVALID", message = Localizer["Error_IDEMPOTENCY_KEY_INVALID"].Value });
            }

            var now = DateTimeOffset.UtcNow;
            var op = await Operations.FindAsync(user.Id, operationId, HttpContext.RequestAborted);
            if (op != null && op.State == ApiOperationState.Running && op.LeaseUntil < now)
            {
                // Do not report a dead attempt as running: expire it (returns its credits) first.
                await Operations.ExpireAsync(op, now);
                op = await Operations.FindAsync(user.Id, operationId, HttpContext.RequestAborted);
            }
            if (op == null)
            {
                return NotFound(new { code = "OPERATION_NOT_FOUND", message = Localizer["Error_OPERATION_NOT_FOUND"].Value });
            }

            return Ok(new
            {
                operationId = op.OperationId,
                operation = op.Operation,
                state = op.State,
                attempts = op.Attempts,
                creditsCharged = op.CreditsCharged,
                creditsReserved = op.CreditsReserved,
                errorCode = op.ErrorCode,
                // Same rule as a replay: stored and not past its retention.
                resultAvailable = op.State == ApiOperationState.Completed && op.ResultProtected != null && op.ResultExpiresAt > now,
                createdAt = op.CreatedAt,
                updatedAt = op.UpdatedAt,
                completedAt = op.CompletedAt,
                leaseUntil = op.LeaseUntil,
            });
        }
    }
}
