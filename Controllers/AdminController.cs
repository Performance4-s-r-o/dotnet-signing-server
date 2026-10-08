using DotNetSigningServer.Data;
using DotNetSigningServer.Middleware;
using DotNetSigningServer.Models;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice;
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

[Authorize(Policy = "AdminOnly")]
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
                CreditsRemaining = u.CreditsRemaining,
                AutoRechargeEnabled = u.AutoRechargeEnabled,
                CreatedAt = u.CreatedAt,
            })
            .ToListAsync();

        ViewBag.Search = search;
        ViewBag.Backoffice = BackofficeStatus.From(_backofficeOptions.Value);
        ViewBag.Outbox = await ReadOutboxAsync();
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

        var vm = new AdminUserDetailViewModel
        {
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
            await _dbContext.SaveChangesAsync();
            TempData["Info"] = _localizer["EnterpriseEnabled"].Value;
        }
        else
        {
            // Disabling enterprise mode — user goes back to pay-as-you-go with zero credits.
            // The enterprise-tracked usage is billed separately (manual invoice), so any
            // credits that remain from pre-enterprise purchases are discarded at switch-off.
            user.IsEnterprise = false;
            user.EnterpriseEnabledAt = null;
            user.CreditsRemaining = 0;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync();
            TempData["Info"] = _localizer["EnterpriseDisabled"].Value;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Grants or withdraws access to /api/seal. Kept separate from the enterprise
    /// toggle on purpose: that one is about how an account is billed, this one is
    /// about whether it may sign with our certificate.
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
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Seal permission for user {UserId} set to {SealAllowed}", user.Id, user.SealAllowed);

        TempData["Info"] = user.SealAllowed
            ? _localizer["SealAllowedEnabled"].Value
            : _localizer["SealAllowedDisabled"].Value;

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

        user.ConcurrencyQueueTimeoutSeconds = queueTimeoutSeconds < 0 ? null : queueTimeoutSeconds;
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
    }
}
