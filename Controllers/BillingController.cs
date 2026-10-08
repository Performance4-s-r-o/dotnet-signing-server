using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Services.Pricing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace DotNetSigningServer.Controllers;

[Authorize]
public class BillingController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IBillingService _billingService;
    private readonly ICreditPricingProvider _pricing;
    private readonly IPaymentGateway _payments;
    private readonly IAutoRechargeService _autoRechargeService;
    private readonly ILogger<BillingController> _logger;
    private readonly IStringLocalizer<SharedStrings> _localizer;

    public BillingController(
        ApplicationDbContext dbContext,
        IBillingService billingService,
        ICreditPricingProvider pricing,
        IPaymentGateway payments,
        IAutoRechargeService autoRechargeService,
        ILogger<BillingController> logger,
        IStringLocalizer<SharedStrings> localizer)
    {
        _dbContext = dbContext;
        _billingService = billingService;
        _pricing = pricing;
        _payments = payments;
        _autoRechargeService = autoRechargeService;
        _logger = logger;
        _localizer = localizer;
    }

    [HttpGet("/Billing")]
    public async Task<IActionResult> Index()
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return RedirectToAction("SignIn", "Account");
        }

        // Always re-read the user row so CreditsRemaining reflects latest usage/purchases
        var user = await _dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null)
        {
            return RedirectToAction("SignIn", "Account");
        }

        var invoices = await _dbContext.Invoices
            .Include(i => i.User)
            .AsNoTracking()
            .Where(i => i.UserId == userId.Value)
            .OrderByDescending(i => i.CreatedAt)
            .Take(20)
            .ToListAsync();

        IReadOnlyList<InvoiceSummary> stripeInvoices = Array.Empty<InvoiceSummary>();
        if (!string.IsNullOrWhiteSpace(user.StripeCustomerId))
        {
            try
            {
                stripeInvoices = await _payments.ListInvoicesAsync(user, 10);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Stripe invoices for user {UserId}", user.Id);
                TempData["Error"] = TempData["Error"] ?? _localizer["StripeInvoicesError"].Value;
            }
        }

        // Find last purchase quantity from this user's webhook events
        int lastPurchaseQuantity = 0;
        var userIdString = userId.Value.ToString();
        var lastCheckout = await _dbContext.WebhookEvents
            .Where(w => w.EventType == "checkout.confirm" && w.PayloadJson.Contains(userIdString))
            .OrderByDescending(w => w.ReceivedAt)
            .FirstOrDefaultAsync();
        if (lastCheckout != null)
        {
            // PayloadJson format: {"documents":100,"userId":"..."}
            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    lastCheckout.PayloadJson, @"""documents""\s*:\s*(\d+)");
                if (match.Success)
                {
                    lastPurchaseQuantity = int.Parse(match.Groups[1].Value);
                }
            }
            catch { }
        }

        SavedPaymentMethod? savedPm = null;
        if (!string.IsNullOrWhiteSpace(user.StripeCustomerId))
        {
            savedPm = await _payments.GetSavedPaymentMethodAsync(user);
        }

        var model = new BillingViewModel
        {
            Invoices = invoices,
            PricePer100 = _pricing.PricePer100,
            Currency = _pricing.Currency,
            Packs = _pricing.GetPacks(),
            CreditsRemaining = user.CreditsRemaining,
            StripeInvoices = stripeInvoices,
            AutoRechargeEnabled = user.AutoRechargeEnabled,
            AutoRechargeQuantity = user.AutoRechargeQuantity,
            AutoRechargePricePer100 = user.AutoRechargePricePer100,
            LastPurchaseQuantity = lastPurchaseQuantity,
            SavedPaymentMethod = savedPm,
            IsEnterprise = user.IsEnterprise,
        };

        if (user.IsEnterprise)
        {
            await PopulateEnterpriseUsageAsync(model, user.Id, user.EnterpriseEnabledAt);
        }

        return View(model);
    }

    private async Task PopulateEnterpriseUsageAsync(BillingViewModel model, Guid userId, DateTimeOffset? enterpriseEnabledAt)
    {
        var today = DateTime.UtcNow.Date;
        var windowStart = new DateTimeOffset(today.AddMonths(-11), TimeSpan.Zero); // 12 months including current
        // Usage before enterprise was enabled was paid via credits — exclude it.
        var since = enterpriseEnabledAt.HasValue && enterpriseEnabledAt.Value > windowStart
            ? enterpriseEnabledAt.Value
            : windowStart;

        var records = await _dbContext.UsageRecords
            .AsNoTracking()
            .Where(u => u.UserId == userId && u.CreatedAt >= since)
            .Select(u => new { u.Count, u.CreatedAt })
            .ToListAsync();

        var byDay = records.ToLookup(r => r.CreatedAt.UtcDateTime.Date, r => r.Count);

        model.UsageToday = byDay[today].Sum();
        model.UsageLast7Days = Enumerable.Range(0, 7).Sum(i => byDay[today.AddDays(-i)].Sum());
        model.UsageLast30Days = Enumerable.Range(0, 30).Sum(i => byDay[today.AddDays(-i)].Sum());

        model.DailyUsage = Enumerable.Range(0, 14)
            .Select(i => today.AddDays(-i))
            .Select(d => new UsageBucket { PeriodStart = d, Credits = byDay[d].Sum() })
            .ToList();

        var firstOfMonth = new DateTime(today.Year, today.Month, 1);
        model.MonthlyUsage = Enumerable.Range(0, 12)
            .Select(i => firstOfMonth.AddMonths(-i))
            .Select(m => new UsageBucket
            {
                PeriodStart = m,
                Credits = records
                    .Where(r =>
                    {
                        var d = r.CreatedAt.UtcDateTime;
                        return d.Year == m.Year && d.Month == m.Month;
                    })
                    .Sum(r => r.Count),
            })
            .ToList();
    }

    [HttpPost("/Billing/ManagePaymentMethod")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ManagePaymentMethod()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return RedirectToAction("SignIn", "Account");

        var user = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null || string.IsNullOrWhiteSpace(user.StripeCustomerId))
        {
            TempData["Error"] = _localizer["NoSavedPaymentMethod"].Value;
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var returnUrl = Url.Action(nameof(Index), "Billing", null, Request.Scheme) ?? "/";
            var portalUrl = await _payments.CreatePortalSessionAsync(user, returnUrl);
            if (string.IsNullOrWhiteSpace(portalUrl))
            {
                TempData["Error"] = _localizer["PaymentStartFailed"].Value;
                return RedirectToAction(nameof(Index));
            }
            return Redirect(portalUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Stripe billing portal session for user {UserId}", user.Id);
            TempData["Error"] = _localizer["PaymentStartFailed"].Value;
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("/Billing/Checkout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Checkout(int documentsToBuy, bool autoRecharge = false, bool saveCard = false)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return RedirectToAction("SignIn", "Account");
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null)
        {
            TempData["Error"] = _localizer["UserNotFound"].Value;
            return RedirectToAction(nameof(Index));
        }

        if (user.IsEnterprise)
        {
            TempData["Error"] = _localizer["EnterpriseBillingManaged"].Value;
            return RedirectToAction(nameof(Index));
        }

        if (string.IsNullOrWhiteSpace(user.StripeCustomerId))
        {
            try
            {
                await _payments.EnsureCustomerAsync(user);
                await _dbContext.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Stripe customer for user {UserId}", user.Id);
                TempData["Error"] = _localizer["StripeCustomerError"].Value;
                return RedirectToAction(nameof(Index));
            }
        }

        if (documentsToBuy <= 0)
        {
            TempData["Error"] = _localizer["SelectAtLeastOneDocument"].Value;
            return RedirectToAction(nameof(Index));
        }

        // Only the sold packs, at the current price (Modules:Pricing: the service's price list
        // snapshot or Billing:*). No call to the service here.
        var pack = _pricing.GetPack(documentsToBuy);
        if (pack == null)
        {
            TempData["Error"] = _localizer["InvalidDocumentBundle"].Value;
            return RedirectToAction(nameof(Index));
        }

        var successUrl = Url.Action("ConfirmCheckout", "Billing", null, Request.Scheme) ?? "/";
        successUrl += successUrl.Contains('?') ? "&session_id={CHECKOUT_SESSION_ID}" : "?session_id={CHECKOUT_SESSION_ID}";
        var cancelUrl = Url.Action("Index", "Billing", null, Request.Scheme) ?? "/";

        try
        {
            // Auto-recharge opt-in implies the card must be saved
            var effectiveSaveCard = saveCard || autoRecharge;

            var checkoutUrl = await _payments.StartCheckoutAsync(
                user,
                pack,
                successUrl,
                cancelUrl,
                new Dictionary<string, string>
                {
                    { "userId", user.Id.ToString() },
                    { "documents", documentsToBuy.ToString() },
                    { "autoRecharge", autoRecharge.ToString() }
                },
                saveCard: effectiveSaveCard);

            if (string.IsNullOrWhiteSpace(checkoutUrl))
            {
                TempData["Error"] = _localizer["CheckoutStartFailed"].Value;
                return RedirectToAction(nameof(Index));
            }

            return Redirect(checkoutUrl);
        }
        catch (BillingApiException ex) when (ex.IsOutage)
        {
            _logger.LogWarning(ex, "Checkout for user {UserId}: payments unavailable", user.Id);
            TempData["Error"] = _localizer["PaymentsTemporarilyUnavailable"].Value;
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stripe checkout creation failed for user {UserId}", user.Id);
            TempData["Error"] = _localizer["PaymentStartFailed"].Value;
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet("/Billing/Checkout/Confirm")]
    public async Task<IActionResult> ConfirmCheckout([FromQuery(Name = "session_id")] string sessionId)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return RedirectToAction("SignIn", "Account");
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            TempData["Error"] = _localizer["MissingStripeSession"].Value;
            return RedirectToAction(nameof(Index));
        }

        try
        {
            // Idempotency check: prevent replay of the same session_id
            var alreadyProcessed = await _dbContext.WebhookEvents.AnyAsync(
                w => w.EventId == sessionId && w.EventType == "checkout.confirm");
            if (alreadyProcessed)
            {
                TempData["Info"] = _localizer["PaymentAlreadyProcessed"].Value;
                return RedirectToAction(nameof(Index));
            }

            var session = await _payments.GetCheckoutAsync(sessionId);
            if (session == null || session.PaymentStatus != "paid")
            {
                TempData["Error"] = _localizer["PaymentNotCompleted"].Value;
                return RedirectToAction(nameof(Index));
            }

            // Verify the checkout session belongs to the currently authenticated user
            if (!BillingCustomerRef.CheckoutBelongsTo(session, userId.Value))
            {
                TempData["Error"] = _localizer["PaymentSessionMismatch"].Value;
                return RedirectToAction(nameof(Index));
            }

            if (!session.Metadata.TryGetValue("documents", out var documentsValue) ||
                !int.TryParse(documentsValue, out var documents) ||
                documents <= 0)
            {
                TempData["Error"] = _localizer["CouldNotDetermineCredits"].Value;
                return RedirectToAction(nameof(Index));
            }

            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
            if (user == null)
            {
                TempData["Error"] = _localizer["UserNotFound"].Value;
                return RedirectToAction(nameof(Index));
            }

            // Record this session as processed — save immediately to claim the
            // idempotency key before granting credits.  If a concurrent webhook
            // tries to process the same session_id, the unique constraint will
            // reject it and prevent double-granting.
            _dbContext.WebhookEvents.Add(new WebhookEvent
            {
                EventId = sessionId,
                EventType = "checkout.confirm",
                PayloadJson = $"{{\"documents\":{documents},\"userId\":\"{userId.Value}\"}}",
                ReceivedAt = DateTimeOffset.UtcNow,
                ProcessedAt = DateTimeOffset.UtcNow
            });

            try
            {
                await _dbContext.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Another handler (webhook) already processed this session
                TempData["Info"] = _localizer["PaymentAlreadyProcessed"].Value;
                return RedirectToAction(nameof(Index));
            }

            // Now safe to grant credits — we own the idempotency key
            await _dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE \"Users\" SET \"CreditsRemaining\" = \"CreditsRemaining\" + {0} WHERE \"Id\" = {1}",
                documents, user.Id);
            await _dbContext.Entry(user).ReloadAsync();

            if (string.IsNullOrWhiteSpace(user.StripeCustomerId) && !string.IsNullOrWhiteSpace(session.StripeCustomerId))
            {
                user.StripeCustomerId = session.StripeCustomerId;
            }

            // Enable auto-recharge if the user opted in during checkout
            if (session.Metadata.TryGetValue("autoRecharge", out var autoRechargeValue)
                && bool.TryParse(autoRechargeValue, out var autoRecharge)
                && autoRecharge)
            {
                await _autoRechargeService.EnableAsync(user, documents, _pricing.PricePer100);
            }

            await _dbContext.SaveChangesAsync();
            TempData["Info"] = _localizer["CreditsAdded", documents].Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to confirm Stripe checkout for session {SessionId}", sessionId);
            TempData["Error"] = _localizer["PaymentConfirmFailed"].Value;
        }

        return RedirectToAction(nameof(Index));
    }

    public class BillingViewModel
    {
        public IEnumerable<DotNetSigningServer.Models.Invoice> Invoices { get; set; } = Enumerable.Empty<DotNetSigningServer.Models.Invoice>();
        public decimal PricePer100 { get; set; }
        public string Currency { get; set; } = "EUR";
        public int CreditsRemaining { get; set; }
        public IReadOnlyList<InvoiceSummary> StripeInvoices { get; set; } = Array.Empty<InvoiceSummary>();
        /// <summary>The sold packs at their current prices, smallest first.</summary>
        public IReadOnlyList<CreditPack> Packs { get; set; } = Array.Empty<CreditPack>();
        public bool AutoRechargeEnabled { get; set; }
        public int AutoRechargeQuantity { get; set; }
        public decimal AutoRechargePricePer100 { get; set; }
        public int LastPurchaseQuantity { get; set; }

        // Saved payment method (card or link) — null if none on file
        public SavedPaymentMethod? SavedPaymentMethod { get; set; }

        // Enterprise mode: credits are billed manually based on tracked usage. UI swaps
        // "remaining credits" for "used credits per period", and hides checkout/auto-recharge controls.
        public bool IsEnterprise { get; set; }
        public int UsageToday { get; set; }
        public int UsageLast7Days { get; set; }
        public int UsageLast30Days { get; set; }
        public IReadOnlyList<UsageBucket> DailyUsage { get; set; } = Array.Empty<UsageBucket>();
        public IReadOnlyList<UsageBucket> MonthlyUsage { get; set; } = Array.Empty<UsageBucket>();
    }

    public class UsageBucket
    {
        public DateTime PeriodStart { get; set; }
        public int Credits { get; set; }
    }

    [HttpPost("/Billing/AutoRecharge/Enable")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnableAutoRecharge(int quantity)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return RedirectToAction("SignIn", "Account");

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null) return RedirectToAction("SignIn", "Account");

        if (user.IsEnterprise)
        {
            TempData["Error"] = _localizer["EnterpriseBillingManaged"].Value;
            return RedirectToAction(nameof(Index));
        }

        if (!CreditPricing.IsSoldQuantity(quantity))
        {
            TempData["Error"] = _localizer["InvalidDocumentBundle"].Value;
            return RedirectToAction(nameof(Index));
        }

        // Ensure user has a Stripe customer
        if (string.IsNullOrWhiteSpace(user.StripeCustomerId))
        {
            try
            {
                await _payments.EnsureCustomerAsync(user);
                await _dbContext.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Stripe customer for user {UserId}", user.Id);
                TempData["Error"] = _localizer["StripeCustomerError"].Value;
                return RedirectToAction(nameof(Index));
            }
        }

        // Check if the customer already has a saved payment method
        bool hasCard = false;
        try
        {
            hasCard = await _payments.HasSavedCardAsync(user);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to verify payment methods for user {UserId}", user.Id);
        }

        // No saved card → redirect to Stripe Checkout in setup mode to collect a card
        if (!hasCard)
        {
            var successUrl = Url.Action(nameof(ConfirmAutoRechargeSetup), "Billing", null, Request.Scheme) ?? "/";
            successUrl += successUrl.Contains('?') ? "&session_id={CHECKOUT_SESSION_ID}" : "?session_id={CHECKOUT_SESSION_ID}";
            var cancelUrl = Url.Action(nameof(Index), "Billing", null, Request.Scheme) ?? "/";

            try
            {
                var setupUrl = await _payments.StartCardSetupAsync(
                    user,
                    successUrl,
                    cancelUrl,
                    new Dictionary<string, string>
                    {
                        { "userId", user.Id.ToString() },
                        { "autoRechargeQuantity", quantity.ToString() }
                    });

                if (string.IsNullOrWhiteSpace(setupUrl))
                {
                    TempData["Error"] = _localizer["PaymentStartFailed"].Value;
                    return RedirectToAction(nameof(Index));
                }

                return Redirect(setupUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Stripe setup session for user {UserId}", user.Id);
                TempData["Error"] = _localizer["PaymentStartFailed"].Value;
                return RedirectToAction(nameof(Index));
            }
        }

        await _autoRechargeService.EnableAsync(user, quantity, _pricing.PricePer100);
        TempData["Info"] = _localizer["AutoRechargeEnabled"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("/Billing/AutoRecharge/Setup/Confirm")]
    public async Task<IActionResult> ConfirmAutoRechargeSetup([FromQuery(Name = "session_id")] string sessionId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return RedirectToAction("SignIn", "Account");

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            TempData["Error"] = _localizer["MissingStripeSession"].Value;
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var session = await _payments.GetCheckoutAsync(sessionId);
            if (session == null || session.Mode != "setup" || session.Status != "complete")
            {
                TempData["Error"] = _localizer["PaymentNotCompleted"].Value;
                return RedirectToAction(nameof(Index));
            }

            if (!BillingCustomerRef.CheckoutBelongsTo(session, userId.Value))
            {
                TempData["Error"] = _localizer["PaymentSessionMismatch"].Value;
                return RedirectToAction(nameof(Index));
            }

            if (!session.Metadata.TryGetValue("autoRechargeQuantity", out var qtyValue)
                || !int.TryParse(qtyValue, out var quantity)
                || !CreditPricing.IsSoldQuantity(quantity))
            {
                TempData["Error"] = _localizer["InvalidDocumentBundle"].Value;
                return RedirectToAction(nameof(Index));
            }

            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
            if (user == null) return RedirectToAction("SignIn", "Account");

            if (string.IsNullOrWhiteSpace(user.StripeCustomerId) && !string.IsNullOrWhiteSpace(session.StripeCustomerId))
            {
                user.StripeCustomerId = session.StripeCustomerId;
                await _dbContext.SaveChangesAsync();
            }

            await _autoRechargeService.EnableAsync(user, quantity, _pricing.PricePer100);
            TempData["Info"] = _localizer["AutoRechargeEnabled"].Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to confirm auto-recharge setup for session {SessionId}", sessionId);
            TempData["Error"] = _localizer["PaymentConfirmFailed"].Value;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Billing/AutoRecharge/Disable")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisableAutoRecharge()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return RedirectToAction("SignIn", "Account");

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null) return RedirectToAction("SignIn", "Account");

        if (user.IsEnterprise)
        {
            TempData["Error"] = _localizer["EnterpriseBillingManaged"].Value;
            return RedirectToAction(nameof(Index));
        }

        await _autoRechargeService.DisableAsync(user);
        TempData["Info"] = _localizer["AutoRechargeDisabled"].Value;
        return RedirectToAction(nameof(Index));
    }

    [AllowAnonymous]
    [HttpGet("/Billing/AutoRecharge/Cancel")]
    public IActionResult ConfirmCancelAutoRecharge([FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            TempData["Error"] = _localizer["InvalidCancelToken"].Value;
            return RedirectToAction(nameof(Index));
        }
        ViewBag.Token = token;
        return View("ConfirmCancelAutoRecharge");
    }

    [AllowAnonymous]
    [HttpPost("/Billing/AutoRecharge/Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelAutoRechargeByToken([FromForm] string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            TempData["Error"] = _localizer["InvalidCancelToken"].Value;
            return RedirectToAction(nameof(Index));
        }

        await _autoRechargeService.DisableByTokenAsync(token);
        TempData["Info"] = _localizer["AutoRechargeDisabled"].Value;
        return RedirectToAction(nameof(Index));
    }

    private Guid? GetCurrentUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out var guid) ? guid : null;
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // PostgreSQL unique violation = 23505
        return ex.InnerException?.Message.Contains("23505") == true
            || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true;
    }
}
