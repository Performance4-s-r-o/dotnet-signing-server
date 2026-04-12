using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System.Security.Claims;
using Stripe;

namespace DotNetSigningServer.Controllers;

[Authorize(Policy = "AdminOnly")]
public class AdminController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAutoRechargeService _autoRechargeService;
    private readonly ILogger<AdminController> _logger;
    private readonly IStringLocalizer<SharedStrings> _localizer;

    public AdminController(
        ApplicationDbContext dbContext,
        IAutoRechargeService autoRechargeService,
        ILogger<AdminController> logger,
        IStringLocalizer<SharedStrings> localizer)
    {
        _dbContext = dbContext;
        _autoRechargeService = autoRechargeService;
        _logger = logger;
        _localizer = localizer;
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
                CreditsRemaining = u.CreditsRemaining,
                AutoRechargeEnabled = u.AutoRechargeEnabled,
                CreatedAt = u.CreatedAt,
            })
            .ToListAsync();

        ViewBag.Search = search;
        return View(users);
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
                    var pmService = new PaymentMethodService();
                    var methods = await pmService.ListAsync(new PaymentMethodListOptions
                    {
                        Customer = user.StripeCustomerId,
                        Type = "card",
                        Limit = 100,
                    });
                    foreach (var pm in methods.Data)
                    {
                        try
                        {
                            await pmService.DetachAsync(pm.Id);
                            _logger.LogInformation("Detached payment method {PmId} for enterprise user {UserId}", pm.Id, user.Id);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to detach payment method {PmId} for user {UserId}", pm.Id, user.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to list/detach payment methods for user {UserId}", user.Id);
                }
            }

            user.IsEnterprise = true;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync();
            TempData["Info"] = _localizer["EnterpriseEnabled"].Value;
        }
        else
        {
            // Disabling enterprise mode — user goes back to pay-as-you-go
            user.IsEnterprise = false;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync();
            TempData["Info"] = _localizer["EnterpriseDisabled"].Value;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    public class AdminUserRow
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = "";
        public bool IsActive { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsEnterprise { get; set; }
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
