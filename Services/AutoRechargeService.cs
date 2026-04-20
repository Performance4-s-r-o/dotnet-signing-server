using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;

namespace DotNetSigningServer.Services;

public class AutoRechargeService : IAutoRechargeService
{
    public const int ThresholdCredits = 10;

    private readonly ApplicationDbContext _dbContext;
    private readonly IBillingService _billingService;
    private readonly BillingOptions _billingOptions;
    private readonly IEmailSender _emailSender;
    private readonly IEmailTemplateRenderer _emailTemplates;
    private readonly ILogger<AutoRechargeService> _logger;
    private readonly AppOptions _appOptions;

    /// <summary>Tracks last failed auto-recharge attempt per user to implement cooldown.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _failedAttempts = new();

    public AutoRechargeService(
        ApplicationDbContext dbContext,
        IBillingService billingService,
        IOptions<BillingOptions> billingOptions,
        IEmailSender emailSender,
        IEmailTemplateRenderer emailTemplates,
        ILogger<AutoRechargeService> logger,
        IOptions<AppOptions> appOptions)
    {
        _dbContext = dbContext;
        _billingService = billingService;
        _billingOptions = billingOptions.Value;
        _emailSender = emailSender;
        _emailTemplates = emailTemplates;
        _logger = logger;
        _appOptions = appOptions.Value;
    }

    public async Task<AutoRechargeResult> TryAutoRechargeAsync(Guid userId)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            return new AutoRechargeResult { Success = false, Error = "User not found" };
        }

        if (!user.AutoRechargeEnabled || user.AutoRechargeQuantity <= 0)
        {
            return new AutoRechargeResult { Success = false, Error = "Auto-recharge not enabled" };
        }

        if (string.IsNullOrWhiteSpace(user.StripeCustomerId))
        {
            return new AutoRechargeResult { Success = false, Error = "No Stripe customer" };
        }

        // Trigger when balance drops below the fixed threshold (10 credits)
        if (user.CreditsRemaining >= ThresholdCredits)
        {
            return new AutoRechargeResult { Success = false, Error = "Credits above threshold" };
        }

        // Cooldown: don't retry if the last attempt failed less than 15 minutes ago
        if (_failedAttempts.TryGetValue(userId, out var lastFailed)
            && DateTimeOffset.UtcNow - lastFailed < TimeSpan.FromMinutes(15))
        {
            _logger.LogDebug("Auto-recharge cooldown active for user {UserId}, last failure at {LastFailed}", userId, lastFailed);
            return new AutoRechargeResult { Success = false, Error = "Cooldown active after previous failure" };
        }

        // Find a saved payment method
        var paymentMethodId = await ResolvePaymentMethodAsync(user.StripeCustomerId);
        if (string.IsNullOrWhiteSpace(paymentMethodId))
        {
            _logger.LogWarning("Auto-recharge failed for user {UserId}: no saved payment method", userId);
            await SendRechargeFailedEmailAsync(user, "No saved payment method found. Please update your payment method.");
            return new AutoRechargeResult { Success = false, Error = "No saved payment method" };
        }

        // Calculate amount using the CURRENT price (not the stored one)
        var amount = _billingService.CalculateAmountForDocuments(user.AutoRechargeQuantity, _billingOptions.PricePer100);
        var amountCents = (long)Math.Round(amount * 100, MidpointRounding.AwayFromZero);

        try
        {
            var paymentIntentService = new PaymentIntentService();
            var paymentIntent = await paymentIntentService.CreateAsync(new PaymentIntentCreateOptions
            {
                Amount = amountCents,
                Currency = _billingOptions.Currency.ToLower(),
                Customer = user.StripeCustomerId,
                PaymentMethod = paymentMethodId,
                OffSession = true,
                Confirm = true,
                Metadata = new Dictionary<string, string>
                {
                    { "type", "auto_recharge" },
                    { "userId", userId.ToString() },
                    { "documents", user.AutoRechargeQuantity.ToString() }
                }
            });

            if (paymentIntent.Status != "succeeded")
            {
                _logger.LogWarning("Auto-recharge payment not succeeded for user {UserId}, status: {Status}",
                    userId, paymentIntent.Status);
                await SendRechargeFailedEmailAsync(user, "Payment was not completed. Please check your payment method.");
                return new AutoRechargeResult { Success = false, Error = $"Payment status: {paymentIntent.Status}" };
            }

            // Grant credits atomically
            await _dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE \"Users\" SET \"CreditsRemaining\" = \"CreditsRemaining\" + {0} WHERE \"Id\" = {1}",
                user.AutoRechargeQuantity, user.Id);

            // Record the payment
            _dbContext.Payments.Add(new Payment
            {
                UserId = user.Id,
                StripePaymentIntentId = paymentIntent.Id,
                AmountCents = (int)amountCents,
                Currency = _billingOptions.Currency,
                Status = "succeeded"
            });

            // Idempotency record
            _dbContext.WebhookEvents.Add(new WebhookEvent
            {
                EventId = $"auto_recharge_{paymentIntent.Id}",
                EventType = "auto_recharge",
                PayloadJson = $"{{\"documents\":{user.AutoRechargeQuantity},\"userId\":\"{userId}\"}}",
                ReceivedAt = DateTimeOffset.UtcNow,
                ProcessedAt = DateTimeOffset.UtcNow
            });

            await _dbContext.SaveChangesAsync();

            // Clear cooldown on success
            _failedAttempts.TryRemove(userId, out _);

            _logger.LogInformation("Auto-recharge succeeded for user {UserId}: +{Credits} credits",
                userId, user.AutoRechargeQuantity);

            await SendRechargeSuccessEmailAsync(user, user.AutoRechargeQuantity, amount);

            return new AutoRechargeResult { Success = true, CreditsAdded = user.AutoRechargeQuantity };
        }
        catch (StripeException ex)
        {
            // Record failure timestamp for cooldown
            _failedAttempts[userId] = DateTimeOffset.UtcNow;

            _logger.LogError(ex, "Auto-recharge Stripe error for user {UserId}", userId);
            await SendRechargeFailedEmailAsync(user, $"Payment failed: {ex.Message}");
            return new AutoRechargeResult { Success = false, Error = ex.Message };
        }
    }

    public async Task EnableAsync(User user, int quantity, decimal pricePer100)
    {
        user.AutoRechargeEnabled = true;
        user.AutoRechargeQuantity = quantity;
        user.AutoRechargePricePer100 = pricePer100;
        user.AutoRechargeCancelToken = Guid.NewGuid().ToString("N");
        user.PriceChangeNotifiedAt = null;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Auto-recharge enabled for user {UserId}: {Quantity} credits at {Price}/100",
            user.Id, quantity, pricePer100);
    }

    public async Task DisableAsync(User user)
    {
        user.AutoRechargeEnabled = false;
        user.AutoRechargeQuantity = 0;
        user.AutoRechargePricePer100 = 0m;
        user.AutoRechargeCancelToken = null;
        user.PriceChangeNotifiedAt = null;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Auto-recharge disabled for user {UserId}", user.Id);
    }

    public async Task DisableByTokenAsync(string cancelToken)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.AutoRechargeCancelToken == cancelToken);
        if (user == null)
        {
            _logger.LogWarning("Auto-recharge cancel attempted with invalid token");
            return;
        }

        await DisableAsync(user);
    }

    private async Task<string?> ResolvePaymentMethodAsync(string customerId)
    {
        try
        {
            // Try customer's default payment method
            var customerService = new CustomerService();
            var customer = await customerService.GetAsync(customerId);

            if (!string.IsNullOrWhiteSpace(customer.InvoiceSettings?.DefaultPaymentMethodId))
            {
                return customer.InvoiceSettings.DefaultPaymentMethodId;
            }

            if (!string.IsNullOrWhiteSpace(customer.DefaultSourceId))
            {
                return customer.DefaultSourceId;
            }

            // Fallback: first saved card
            var pmService = new PaymentMethodService();
            var methods = await pmService.ListAsync(new PaymentMethodListOptions
            {
                Customer = customerId,
                Type = "card",
                Limit = 1
            });

            return methods.Data.FirstOrDefault()?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve payment method for customer {CustomerId}", customerId);
            return null;
        }
    }

    private async Task SendRechargeSuccessEmailAsync(User user, int creditsAdded, decimal amount)
    {
        if (!user.EmailNotificationsEnabled)
        {
            return;
        }

        var baseUrl = _appOptions.FqdnServerName?.TrimEnd('/') ?? "https://app.p4pdf.com";
        var billingUrl = $"{baseUrl}/Billing";
        var locale = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var rendered = _emailTemplates.Render(EmailTemplateId.AutoRechargeSuccess, locale, new Dictionary<string, string?>
        {
            ["quantity"] = creditsAdded.ToString(),
            ["amount"] = amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["currency"] = _billingOptions.Currency,
            ["newBalance"] = (user.CreditsRemaining + creditsAdded).ToString(),
            ["billingUrl"] = billingUrl,
        });

        try
        {
            await _emailSender.SendAsync(
                user.Email,
                rendered.Subject,
                rendered.HtmlBody,
                $"{baseUrl}/Account/Settings",
                isCritical: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send auto-recharge success email to {Email}", user.Email);
        }
    }

    private async Task SendRechargeFailedEmailAsync(User user, string reason)
    {
        if (!user.EmailNotificationsEnabled)
        {
            return;
        }

        var baseUrl = _appOptions.FqdnServerName?.TrimEnd('/') ?? "https://app.p4pdf.com";
        var billingUrl = $"{baseUrl}/Billing";
        var locale = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var rendered = _emailTemplates.Render(EmailTemplateId.AutoRechargeFailed, locale, new Dictionary<string, string?>
        {
            ["quantity"] = user.AutoRechargeQuantity.ToString(),
            ["failureReason"] = reason,
            ["currentBalance"] = user.CreditsRemaining.ToString(),
            ["billingUrl"] = billingUrl,
        });

        try
        {
            await _emailSender.SendAsync(
                user.Email,
                rendered.Subject,
                rendered.HtmlBody,
                $"{baseUrl}/Account/Settings",
                isCritical: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send auto-recharge failure email to {Email}", user.Email);
        }
    }
}
