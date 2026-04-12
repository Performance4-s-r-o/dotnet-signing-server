using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using Stripe.Checkout;
using Stripe;

namespace DotNetSigningServer.Controllers;

[Authorize]
public class BillingController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IBillingService _billingService;
    private readonly BillingOptions _billingOptions;
    private readonly IStripeCheckoutService _checkoutService;
    private readonly IAutoRechargeService _autoRechargeService;
    private readonly ILogger<BillingController> _logger;
    private readonly IStringLocalizer<SharedStrings> _localizer;

    public BillingController(
        ApplicationDbContext dbContext,
        IBillingService billingService,
        IOptions<BillingOptions> billingOptions,
        IStripeCheckoutService checkoutService,
        IAutoRechargeService autoRechargeService,
        ILogger<BillingController> logger,
        IStringLocalizer<SharedStrings> localizer)
    {
        _dbContext = dbContext;
        _billingService = billingService;
        _billingOptions = billingOptions.Value;
        _checkoutService = checkoutService;
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

        IReadOnlyList<global::Stripe.Invoice> stripeInvoices = Array.Empty<global::Stripe.Invoice>();
        if (!string.IsNullOrWhiteSpace(user.StripeCustomerId))
        {
            try
            {
                stripeInvoices = await _checkoutService.GetInvoicesAsync(user.StripeCustomerId, 10);
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

        // Fetch saved payment method (brand + last4) if available
        string? cardBrand = null, cardLast4 = null, cardExpiry = null;
        if (!string.IsNullOrWhiteSpace(user.StripeCustomerId))
        {
            var pm = await _checkoutService.GetDefaultPaymentMethodAsync(user.StripeCustomerId);
            if (pm != null)
            {
                cardBrand = pm.Value.Brand;
                cardLast4 = pm.Value.Last4;
                cardExpiry = $"{pm.Value.ExpMonth:D2}/{pm.Value.ExpYear % 100:D2}";
            }
        }

        var model = new BillingViewModel
        {
            Invoices = invoices,
            PricePer100 = _billingOptions.PricePer100,
            Currency = _billingOptions.Currency,
            CreditsRemaining = user.CreditsRemaining,
            StripeInvoices = stripeInvoices,
            Discount300 = _billingOptions.Discount300,
            Discount500 = _billingOptions.Discount500,
            Discount1000 = _billingOptions.Discount1000,
            AutoRechargeEnabled = user.AutoRechargeEnabled,
            AutoRechargeQuantity = user.AutoRechargeQuantity,
            AutoRechargePricePer100 = user.AutoRechargePricePer100,
            LastPurchaseQuantity = lastPurchaseQuantity,
            SavedCardBrand = cardBrand,
            SavedCardLast4 = cardLast4,
            SavedCardExpiry = cardExpiry,
        };

        return View(model);
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
            var portalUrl = await _checkoutService.CreateBillingPortalSessionAsync(user.StripeCustomerId, returnUrl);
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

        if (string.IsNullOrWhiteSpace(user.StripeCustomerId))
        {
            try
            {
                var customerService = new CustomerService();
                var customer = await customerService.CreateAsync(new CustomerCreateOptions
                {
                    Email = user.Email,
                    Metadata = new Dictionary<string, string>
                    {
                        { "app_user_id", user.Id.ToString() }
                    }
                });

                user.StripeCustomerId = customer.Id;
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

        var allowedDocuments = new[] { 100, 300, 500, 1000 };
        if (!allowedDocuments.Contains(documentsToBuy))
        {
            TempData["Error"] = _localizer["InvalidDocumentBundle"].Value;
            return RedirectToAction(nameof(Index));
        }

        var units = (int)Math.Ceiling(documentsToBuy / 100m);
        var baseAmount = units * _billingOptions.PricePer100;

        decimal discount = 0m;
        if (documentsToBuy >= 1000)
        {
            discount = _billingOptions.Discount1000;
        }
        else if (documentsToBuy >= 500)
        {
            discount = _billingOptions.Discount500;
        }
        else if (documentsToBuy >= 300)
        {
            discount = _billingOptions.Discount300;
        }

        var amount = baseAmount - (baseAmount * discount);
        var amountCents = (long)Math.Round(amount * 100, MidpointRounding.AwayFromZero);
        var successUrl = Url.Action("ConfirmCheckout", "Billing", null, Request.Scheme) ?? "/";
        successUrl += successUrl.Contains('?') ? "&session_id={CHECKOUT_SESSION_ID}" : "?session_id={CHECKOUT_SESSION_ID}";
        var cancelUrl = Url.Action("Index", "Billing", null, Request.Scheme) ?? "/";

        try
        {
            // Auto-recharge opt-in implies the card must be saved
            var effectiveSaveCard = saveCard || autoRecharge;

            var checkoutUrl = await _checkoutService.CreateCheckoutSessionAsync(
                user,
                amountCents,
                _billingOptions.Currency,
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

            var session = await _checkoutService.GetSessionAsync(sessionId);
            if (session == null || session.PaymentStatus != "paid")
            {
                TempData["Error"] = _localizer["PaymentNotCompleted"].Value;
                return RedirectToAction(nameof(Index));
            }

            // Verify the checkout session belongs to the currently authenticated user
            if (session.Metadata.TryGetValue("userId", out var metaUserId)
                && metaUserId != userId.Value.ToString())
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

            if (string.IsNullOrWhiteSpace(user.StripeCustomerId) && !string.IsNullOrWhiteSpace(session.CustomerId))
            {
                user.StripeCustomerId = session.CustomerId;
            }

            // Enable auto-recharge if the user opted in during checkout
            if (session.Metadata.TryGetValue("autoRecharge", out var autoRechargeValue)
                && bool.TryParse(autoRechargeValue, out var autoRecharge)
                && autoRecharge)
            {
                await _autoRechargeService.EnableAsync(user, documents, _billingOptions.PricePer100);
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
        public IReadOnlyList<global::Stripe.Invoice> StripeInvoices { get; set; } = Array.Empty<global::Stripe.Invoice>();
        public decimal Discount300 { get; set; }
        public decimal Discount500 { get; set; }
        public decimal Discount1000 { get; set; }
        public bool AutoRechargeEnabled { get; set; }
        public int AutoRechargeQuantity { get; set; }
        public decimal AutoRechargePricePer100 { get; set; }
        public int LastPurchaseQuantity { get; set; }

        // Saved payment method details (null if no card on file)
        public string? SavedCardBrand { get; set; }
        public string? SavedCardLast4 { get; set; }
        public string? SavedCardExpiry { get; set; }
    }

    [HttpPost("/Billing/AutoRecharge/Enable")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnableAutoRecharge(int quantity)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return RedirectToAction("SignIn", "Account");

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null) return RedirectToAction("SignIn", "Account");

        var allowedQuantities = new[] { 100, 300, 500, 1000 };
        if (!allowedQuantities.Contains(quantity))
        {
            TempData["Error"] = _localizer["InvalidDocumentBundle"].Value;
            return RedirectToAction(nameof(Index));
        }

        if (string.IsNullOrWhiteSpace(user.StripeCustomerId))
        {
            TempData["Error"] = _localizer["AutoRechargeRequiresPayment"].Value;
            return RedirectToAction(nameof(Index));
        }

        // Verify the customer actually has a saved payment method
        try
        {
            var pmService = new Stripe.PaymentMethodService();
            var methods = await pmService.ListAsync(new Stripe.PaymentMethodListOptions
            {
                Customer = user.StripeCustomerId,
                Type = "card",
                Limit = 1
            });
            if (methods.Data.Count == 0)
            {
                TempData["Error"] = _localizer["AutoRechargeRequiresPayment"].Value;
                return RedirectToAction(nameof(Index));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to verify payment methods for user {UserId}", user.Id);
            TempData["Error"] = _localizer["AutoRechargeRequiresPayment"].Value;
            return RedirectToAction(nameof(Index));
        }

        await _autoRechargeService.EnableAsync(user, quantity, _billingOptions.PricePer100);
        TempData["Info"] = _localizer["AutoRechargeEnabled"].Value;
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
