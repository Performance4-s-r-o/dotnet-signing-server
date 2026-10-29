using DotNetSigningServer.Data;
using DotNetSigningServer.Middleware;
using DotNetSigningServer.Models;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Consents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using DotNetSigningServer.Services.Billing;

namespace DotNetSigningServer.Controllers;

[CookieAuthorize(Policy = "AdminOnly")]
public class AdminController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IPaymentGateway _payments;
    private readonly IAutoRechargeService _autoRechargeService;
    private readonly ILogger<AdminController> _logger;
    private readonly IStringLocalizer<SharedStrings> _localizer;
    private readonly IOptions<P4BackofficeProductOptions> _backofficeOptions;
    private readonly OutboxSignal _outboxSignal;
    private readonly TimeProvider _time;

    public AdminController(
        ApplicationDbContext dbContext,
        IAutoRechargeService autoRechargeService,
        IPaymentGateway payments,
        ILogger<AdminController> logger,
        IStringLocalizer<SharedStrings> localizer,
        IOptions<P4BackofficeProductOptions> backofficeOptions,
        OutboxSignal outboxSignal,
        TimeProvider time)
    {
        _outboxSignal = outboxSignal;
        _time = time;
        _dbContext = dbContext;
        _autoRechargeService = autoRechargeService;
        _payments = payments;
        _logger = logger;
        _localizer = localizer;
        _backofficeOptions = backofficeOptions;
    }

    [HttpGet("/Admin")]
    public async Task<IActionResult> Index(string? search = null)
    {
        var query = _dbContext.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmed = search.Trim().ToLower();
            query = query.Where(u => u.Email.ToLower().Contains(trimmed));
        }

        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .Take(200)
            .Select(u => new AdminUserRow
            {
                Id = u.Id,
                Email = u.Email,
                IsActive = u.IsActive,
                IsAdmin = u.IsAdmin,
                IsEnterprise = u.IsEnterprise,
                SealAllowed = u.SealAllowed,
                TrustedCaller = u.TrustedCaller,
                CreditsRemaining = u.CreditsRemaining,
                AutoRechargeEnabled = u.AutoRechargeEnabled,
                CreatedAt = u.CreatedAt,
            })
            .ToListAsync();

        ViewBag.Search = search;
        ViewBag.Backoffice = BackofficeStatus.From(_backofficeOptions.Value);
        ViewBag.Outbox = await ReadOutboxAsync();
        ViewBag.InboxDead = await ReadInboxDeadAsync();
        return View(users);
    }

    /// <summary>
    /// Outbox overview for the admin page. The table may be missing on an instance whose
    /// migrations have not run yet; the page must still render then.
    /// </summary>
    private async Task<OutboxHealthSnapshot?> ReadOutboxAsync()
    {
        try
        {
            return await OutboxHealth.ReadAsync(_dbContext, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Backoffice outbox overview unavailable");
            return null;
        }
    }

    /// <summary>Dead backoffice events for the admin page; null when the table cannot be read.</summary>
    private async Task<int?> ReadInboxDeadAsync()
    {
        try
        {
            return await BackofficeInboxProcessor.CountDeadAsync(_dbContext, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Backoffice inbox overview unavailable");
            return null;
        }
    }

    /// <summary>
    /// Puts dead backoffice events (out of attempts) back in the queue once their cause is fixed —
    /// e.g. a <c>billing.checkout.completed</c> whose credits were never granted (#79).
    /// </summary>
    [HttpPost("/Admin/Backoffice/Inbox/ReplayDead")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReplayDeadInbox()
    {
        var ids = await BackofficeInboxProcessor.ReplayDeadAsync(_dbContext, _time.GetUtcNow(), cancellationToken: HttpContext.RequestAborted);
        AdminAudit.Record(_dbContext, User, null, AdminAudit.Actions.InboxReplay, new { count = ids.Count }, _time.GetUtcNow());
        await _dbContext.SaveChangesAsync();
        if (ids.Count > 0) HttpContext.RequestServices.GetService<BackofficeInboxSignal>()?.Notify();

        _logger.LogInformation("Admin replayed {Count} dead backoffice inbox event(s)", ids.Count);
        TempData["Info"] = string.Format(_localizer["AdminInboxReplayed"].Value, ids.Count);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Puts items refused as unauthorised (401/403) back in the queue once the API key or its
    /// scopes have been fixed.
    /// </summary>
    [HttpPost("/Admin/Backoffice/Outbox/RequeueBlocked")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequeueBlockedOutbox()
    {
        var ids = await OutboxHealth.RequeueBlockedAsync(_dbContext, _time.GetUtcNow(), HttpContext.RequestAborted);
        foreach (var id in ids) _outboxSignal.Notify(id);
        AdminAudit.Record(_dbContext, User, null, AdminAudit.Actions.OutboxRequeue, new { count = ids.Count }, _time.GetUtcNow());
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Admin requeued {Count} blocked backoffice outbox item(s)", ids.Count);
        TempData["Info"] = string.Format(_localizer["AdminOutboxRequeued"].Value, ids.Count);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// One-off catch-up after the Consents module was Off: queues the consent records that were
    /// only stored locally (see <see cref="ConsentBackfill"/>). Safe to repeat.
    /// </summary>
    [HttpPost("/Admin/Backoffice/Consents/Backfill")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BackfillConsents([FromServices] ConsentBackfill backfill)
    {
        if (_backofficeOptions.Value.ModeFor(BackofficeModule.Consents) == BackofficeMode.Off)
        {
            TempData["Error"] = _localizer["AdminConsentsBackfillOff"].Value;
            return RedirectToAction(nameof(Index));
        }

        var result = await backfill.RunAsync(HttpContext.RequestAborted);
        AdminAudit.Record(_dbContext, User, null, AdminAudit.Actions.ConsentsBackfill,
            new { records = result.Records, batches = result.Batches }, _time.GetUtcNow());
        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Admin queued {Records} consent record(s) in {Batches} batch(es)", result.Records, result.Batches);
        TempData["Info"] = string.Format(_localizer["AdminConsentsBackfilled"].Value, result.Records, result.Batches);
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("/Admin/Users/{id:guid}")]
    public async Task<IActionResult> Details(Guid id)
    {
        var user = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        if (user == null)
        {
            TempData["Error"] = _localizer["UserNotFound"].Value;
            return RedirectToAction(nameof(Index));
        }

        // Aggregate usage for the current month and the last 6 months
        var now = DateTimeOffset.UtcNow;
        var startOfMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var sixMonthsAgo = startOfMonth.AddMonths(-5);

        var usageThisMonth = await _dbContext.UsageRecords
            .AsNoTracking()
            .Where(r => r.UserId == id && r.CreatedAt >= startOfMonth)
            .SumAsync(r => (int?)r.Count) ?? 0;

        var monthlyBreakdown = await _dbContext.UsageRecords
            .AsNoTracking()
            .Where(r => r.UserId == id && r.CreatedAt >= sixMonthsAgo)
            .GroupBy(r => new { r.CreatedAt.Year, r.CreatedAt.Month })
            .Select(g => new MonthlyUsage
            {
                Year = g.Key.Year,
                Month = g.Key.Month,
                TotalCredits = g.Sum(r => r.Count),
                OperationCount = g.Count(),
            })
            .OrderByDescending(m => m.Year).ThenByDescending(m => m.Month)
            .ToListAsync();

        var byOperation = await _dbContext.UsageRecords
            .AsNoTracking()
            .Where(r => r.UserId == id && r.CreatedAt >= startOfMonth)
            .GroupBy(r => r.Operation)
            .Select(g => new OperationBreakdown
            {
                Operation = g.Key ?? "unknown",
                Count = g.Count(),
                TotalCredits = g.Sum(r => r.Count),
            })
            .OrderByDescending(o => o.TotalCredits)
            .ToListAsync();

        var audit = await _dbContext.AdminAuditEntries.AsNoTracking()
            .Where(a => a.TargetUserId == id)
            .OrderByDescending(a => a.CreatedAt)
            .Take(50)
            .ToListAsync();

        var vm = new AdminUserDetailViewModel
        {
            Audit = audit,
            User = user,
            UsageThisMonth = usageThisMonth,
            MonthlyBreakdown = monthlyBreakdown,
            ByOperation = byOperation,
        };

        return View(vm);
    }

    [HttpPost("/Admin/Users/{id:guid}/ToggleEnterprise")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleEnterprise(Guid id)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null)
        {
            TempData["Error"] = _localizer["UserNotFound"].Value;
            return RedirectToAction(nameof(Index));
        }

        if (!user.IsEnterprise)
        {
            // Enabling enterprise mode:
            // 1. Disable auto-recharge
            // 2. Detach all saved payment methods from Stripe
            // 3. Set IsEnterprise flag
            var wasAutoRecharge = user.AutoRechargeEnabled;
            if (user.AutoRechargeEnabled)
            {
                await _autoRechargeService.DisableAsync(user);
            }

            if (!string.IsNullOrWhiteSpace(user.StripeCustomerId))
            {
                try
                {
                    await _payments.DetachPaymentMethodsAsync(user);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to list/detach payment methods for user {UserId}", user.Id);
                }
            }

            user.IsEnterprise = true;
            user.EnterpriseEnabledAt = DateTimeOffset.UtcNow;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            AdminAudit.Record(_dbContext, User, user.Id, AdminAudit.Actions.Enterprise,
                new { from = false, to = true, autoRechargeDisabled = wasAutoRecharge }, _time.GetUtcNow());
            await _dbContext.SaveChangesAsync();
            TempData["Info"] = _localizer["EnterpriseEnabled"].Value;
        }
        else
        {
            // Disabling enterprise mode — user goes back to pay-as-you-go with zero credits.
            // The enterprise-tracked usage is billed separately (manual invoice), so any
            // credits that remain from pre-enterprise purchases are discarded at switch-off.
            var discarded = user.CreditsRemaining;
            user.IsEnterprise = false;
            user.EnterpriseEnabledAt = null;
            user.CreditsRemaining = 0;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            AdminAudit.Record(_dbContext, User, user.Id, AdminAudit.Actions.Enterprise,
                new { from = true, to = false, creditsDiscarded = discarded }, _time.GetUtcNow());
            await _dbContext.SaveChangesAsync();
            TempData["Info"] = _localizer["EnterpriseDisabled"].Value;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Grants or withdraws access to /api/seal. Kept separate from the enterprise
    /// toggle on purpose: that one is about how an account is billed. Since #122
    /// the endpoint only answers 422 SEAL_CERTIFICATE_REQUIRED.
    /// </summary>
    [HttpPost("/Admin/Users/{id:guid}/ToggleSealAllowed")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleSealAllowed(Guid id)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null)
        {
            TempData["Error"] = _localizer["UserNotFound"].Value;
            return RedirectToAction(nameof(Index));
        }

        user.SealAllowed = !user.SealAllowed;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        // Permission changes stay audited: who granted it matters as much as to whom.
        AdminAudit.Record(_dbContext, User, user.Id, AdminAudit.Actions.SealAllowed,
            new { from = !user.SealAllowed, to = user.SealAllowed }, _time.GetUtcNow());
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Seal permission for user {UserId} set to {SealAllowed} by {ActorId}",
            user.Id, user.SealAllowed, User.FindFirstValue(ClaimTypes.NameIdentifier));

        TempData["Info"] = user.SealAllowed
            ? _localizer["SealAllowedEnabled"].Value
            : _localizer["SealAllowedDisabled"].Value;

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Marks or unmarks the trusted internal caller's account (#78). Only an administrator, and
    /// never on their own account: whoever holds the flag gets the tenant headers believed.
    /// </summary>
    [HttpPost("/Admin/Users/{id:guid}/ToggleTrustedCaller")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleTrustedCaller(Guid id)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null)
        {
            TempData["Error"] = _localizer["UserNotFound"].Value;
            return RedirectToAction(nameof(Index));
        }

        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!user.TrustedCaller && string.Equals(actorId, user.Id.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = _localizer["TrustedCallerNotOnSelf"].Value;
            return RedirectToAction(nameof(Details), new { id });
        }

        user.TrustedCaller = !user.TrustedCaller;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        AdminAudit.Record(_dbContext, User, user.Id, AdminAudit.Actions.TrustedCaller,
            new { from = !user.TrustedCaller, to = user.TrustedCaller }, _time.GetUtcNow());
        await _dbContext.SaveChangesAsync();

        // The token validation cache holds the account as it was: drop it so the change applies now.
        var apiAuth = HttpContext.RequestServices?.GetService<IApiAuthService>();
        if (apiAuth != null)
        {
            var tokenIds = await _dbContext.ApiTokens.Where(t => t.UserId == user.Id).Select(t => t.Id).ToListAsync();
            foreach (var tokenId in tokenIds) apiAuth.InvalidateTokenCache(tokenId);
        }

        _logger.LogWarning(
            "Trusted caller flag for user {UserId} set to {TrustedCaller} by {ActorId}",
            user.Id, user.TrustedCaller, actorId);

        TempData["Info"] = user.TrustedCaller
            ? _localizer["TrustedCallerEnabled"].Value
            : _localizer["TrustedCallerDisabled"].Value;

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("/Admin/Users/{id:guid}/SetConcurrencyQueueTimeout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetConcurrencyQueueTimeout(Guid id, [FromForm] int? queueTimeoutSeconds)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null)
        {
            TempData["Error"] = _localizer["UserNotFound"].Value;
            return RedirectToAction(nameof(Index));
        }

        // An operator may go past the user's cap, which binds only the trusted caller at run time.
        if (queueTimeoutSeconds is < -1 or > BillingOptions.MaxQueueSeconds)
        {
            TempData["Error"] = _localizer["InvalidQueueTimeout", BillingOptions.MaxQueueSeconds].Value;
            return RedirectToAction(nameof(Details), new { id });
        }

        var before = user.ConcurrencyQueueTimeoutSeconds;
        user.ConcurrencyQueueTimeoutSeconds = queueTimeoutSeconds < 0 ? null : queueTimeoutSeconds;
        AdminAudit.Record(_dbContext, User, user.Id, AdminAudit.Actions.ConcurrencyQueueTimeout,
            new { from = before, to = user.ConcurrencyQueueTimeoutSeconds }, _time.GetUtcNow());
        await _dbContext.SaveChangesAsync();

        UserConcurrencyMiddleware.InvalidateLimitCache(id);
        TempData["Info"] = _localizer["ConcurrencyQueueTimeoutUpdated"].Value;
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>What the admin overview shows of the backoffice integration — never the key itself.</summary>
    public class BackofficeStatus
    {
        public IReadOnlyList<(BackofficeModule Module, BackofficeMode Mode)> Modules { get; init; } = [];
        public string? BaseUrl { get; init; }
        public string? KeyDisplay { get; init; }
        public BackofficeDisabledReason DisabledReason { get; init; }

        public static BackofficeStatus From(P4BackofficeProductOptions options) => new()
        {
            Modules = Enum.GetValues<BackofficeModule>().Select(m => (m, options.ModeFor(m))).ToList(),
            BaseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? null : options.BaseUrl.Trim(),
            KeyDisplay = BackofficeOptionsValidator.KeyDisplay(options.SecretKey),
            DisabledReason = options.DisabledReason,
        };
    }

    public class AdminUserRow
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = "";
        public bool IsActive { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsEnterprise { get; set; }
        public bool SealAllowed { get; set; }
        public bool TrustedCaller { get; set; }
        public int CreditsRemaining { get; set; }
        public bool AutoRechargeEnabled { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    public class MonthlyUsage
    {
        public int Year { get; set; }
        public int Month { get; set; }
        public int TotalCredits { get; set; }
        public int OperationCount { get; set; }
    }

    public class OperationBreakdown
    {
        public string Operation { get; set; } = "";
        public int Count { get; set; }
        public int TotalCredits { get; set; }
    }

    public class AdminUserDetailViewModel
    {
        public User User { get; set; } = null!;
        public int UsageThisMonth { get; set; }
        public List<MonthlyUsage> MonthlyBreakdown { get; set; } = new();
        public List<OperationBreakdown> ByOperation { get; set; } = new();
        public List<AdminAuditEntry> Audit { get; set; } = new();
    }
}
