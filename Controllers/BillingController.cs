using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    private readonly ILogger<BillingController> _logger;

    public BillingController(
        ApplicationDbContext dbContext,
        IBillingService billingService,
        IOptions<BillingOptions> billingOptions,
        IStripeCheckoutService checkoutService,
        ILogger<BillingController> logger)
    {
        _dbContext = dbContext;
        _billingService = billingService;
        _billingOptions = billingOptions.Value;
        _checkoutService = checkoutService;
        _logger = logger;
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
                TempData["Error"] = TempData["Error"] ?? "Could not load Stripe invoices.";
            }
        }

        var model = new BillingViewModel
        {
            Invoices = invoices,
            PricePer100 = _billingOptions.PricePer100,
            Currency = _billingOptions.Currency,
            CreditsRemaining = user.CreditsRemaining,
            StripeInvoices = stripeInvoices
        };

        return View(model);
    }

    [HttpPost("/Billing/Checkout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Checkout(int documentsToBuy)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return RedirectToAction("SignIn", "Account");
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null)
        {
            TempData["Error"] = "User not found.";
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
                TempData["Error"] = "Could not create Stripe customer. Please try again.";
                return RedirectToAction(nameof(Index));
            }
        }

        if (documentsToBuy <= 0)
        {
            TempData["Error"] = "Select at least 1 document to purchase credits.";
            return RedirectToAction(nameof(Index));
        }

        var allowedDocuments = new[] { 100, 300, 500, 1000 };
        if (!allowedDocuments.Contains(documentsToBuy))
        {
            TempData["Error"] = "Invalid document bundle selected.";
            return RedirectToAction(nameof(Index));
        }

        var units = (int)Math.Ceiling(documentsToBuy / 100m);
        var amount = units * _billingOptions.PricePer100;
        var amountCents = (long)Math.Ceiling(amount * 100);
        var successUrl = Url.Action("ConfirmCheckout", "Billing", null, Request.Scheme) ?? "/";
        successUrl += successUrl.Contains('?') ? "&session_id={CHECKOUT_SESSION_ID}" : "?session_id={CHECKOUT_SESSION_ID}";
        var cancelUrl = Url.Action("Index", "Billing", null, Request.Scheme) ?? "/";

        try
        {
            var checkoutUrl = await _checkoutService.CreateCheckoutSessionAsync(
                user,
                amountCents,
                _billingOptions.Currency,
                successUrl,
                cancelUrl,
                new Dictionary<string, string>
                {
                    { "userId", user.Id.ToString() },
                    { "documents", documentsToBuy.ToString() }
                });

            if (string.IsNullOrWhiteSpace(checkoutUrl))
            {
                TempData["Error"] = "Unable to start checkout.";
                return RedirectToAction(nameof(Index));
            }

            return Redirect(checkoutUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stripe checkout creation failed for user {UserId}", user.Id);
            TempData["Error"] = "Payment could not be started. Please try again.";
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
            TempData["Error"] = "Missing Stripe session id.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var session = await _checkoutService.GetSessionAsync(sessionId);
            if (session == null || session.PaymentStatus != "paid")
            {
                TempData["Error"] = "Payment not completed.";
                return RedirectToAction(nameof(Index));
            }

            if (!session.Metadata.TryGetValue("documents", out var documentsValue) ||
                !int.TryParse(documentsValue, out var documents) ||
                documents <= 0)
            {
                TempData["Error"] = "Could not determine purchased credits.";
                return RedirectToAction(nameof(Index));
            }

            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
            if (user == null)
            {
                TempData["Error"] = "User not found.";
                return RedirectToAction(nameof(Index));
            }

            user.CreditsRemaining += documents;
            if (string.IsNullOrWhiteSpace(user.StripeCustomerId) && !string.IsNullOrWhiteSpace(session.CustomerId))
            {
                user.StripeCustomerId = session.CustomerId;
            }

            await _dbContext.SaveChangesAsync();
            TempData["Info"] = $"Added {documents} document credits to your account.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to confirm Stripe checkout for session {SessionId}", sessionId);
            TempData["Error"] = "Could not confirm payment.";
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
    }

    private Guid? GetCurrentUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out var guid) ? guid : null;
    }
}
