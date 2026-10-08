using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Services.Pricing;
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
    private readonly ICreditPricingProvider _pricing;
    private readonly ITemplatedEmailSender _email;
    private readonly ILogger<AutoRechargeService> _logger;
    private readonly AppOptions _appOptions;
    // Modules:Billing=On: the charge goes through the billing API instead of Stripe directly.
    private readonly BackofficeAutoRecharge? _viaService;

    /// <summary>Tracks last failed auto-recharge attempt per user to implement cooldown.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _failedAttempts = new();
    // Per-user in-flight guard: stops two concurrent debits that both crossed the
    // threshold from each creating a Stripe charge (per-PaymentIntent idempotency
    // doesn't cover two distinct intents). In-process only — a multi-replica
    // deployment would additionally need a DB/distributed lock.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _inFlight = new();

    public AutoRechargeService(
        ApplicationDbContext dbContext,
        IBillingService billingService,
        IOptions<BillingOptions> billingOptions,
        ITemplatedEmailSender email,
        ILogger<AutoRechargeService> logger,
        IOptions<AppOptions> appOptions,
        ICreditPricingProvider pricing,
        BackofficeAutoRecharge? viaService = null)
    {
        _viaService = viaService;
        _pricing = pricing;
        _dbContext = dbContext;
        _billingService = billingService;
        _billingOptions = billingOptions.Value;
        _email = email;
        _logger = logger;
        _appOptions = appOptions.Value;
    }

    public async Task<AutoRechargeResult> TryAutoRechargeAsync(Guid userId)
    {
        // Only one recharge per user may run at a time (see _inFlight).
        if (!_inFlight.TryAdd(userId, 0))
        {
            return new AutoRechargeResult { Success = false, Error = "Auto-recharge already in progress" };
        }
        try
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

            if (_viaService != null)
            {
                return await ChargeViaServiceAsync(user);
            }

            // Find a saved payment method
            var paymentMethodId = await ResolvePaymentMethodAsync(user.StripeCustomerId);
            if (string.IsNullOrWhiteSpace(paymentMethodId))
            {
                _logger.LogWarning("Auto-recharge failed for user {UserId}: no saved payment method", userId);
                await SendRechargeFailedEmailAsync(user, "No saved payment method found. Please update your payment method.");
                return new AutoRechargeResult { Success = false, Error = "No saved payment method" };
            }

            // The CURRENT price of the pack (not the stored one), from the price list in force
            // (Modules:Pricing) — the same amount Checkout charges. A quantity that is not a sold
            // pack keeps the configured formula.
            var pack = _pricing.GetPack(user.AutoRechargeQuantity);
            var amount = pack?.Amount
                ?? _billingService.CalculateAmountForDocuments(user.AutoRechargeQuantity, _billingOptions.PricePer100);
            var amountCents = pack?.UnitAmountMinor ?? CreditPricing.ToMinorUnits(amount);
            var currency = pack?.Currency ?? _billingOptions.Currency;

            try
            {
                var paymentIntentService = new PaymentIntentService();
                var paymentIntent = await paymentIntentService.CreateAsync(new PaymentIntentCreateOptions
                {
                    Amount = amountCents,
                    Currency = currency.ToLower(),
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
                    Currency = currency,
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

                await SendRechargeSuccessEmailAsync(user, user.AutoRechargeQuantity, amount, currency);

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
        finally
        {
            _inFlight.TryRemove(userId, out _);
        }
    }

    /// <summary>
    /// Modules:Billing=On. Only a pack of the price list in force can be charged (catalog
    /// prices only); the service picks the card and refuses without one.
    /// </summary>
    private async Task<AutoRechargeResult> ChargeViaServiceAsync(User user)
    {
        var pack = _pricing.GetPack(user.AutoRechargeQuantity);
        if (pack?.LookupKey == null)
        {
            _logger.LogError("Auto-recharge of user {UserId}: the {Quantity}-credit pack has no price in the price list in force",
                user.Id, user.AutoRechargeQuantity);
            return new AutoRechargeResult { Success = false, Error = "Pack not in the price list" };
        }

        var charge = await _viaService!.ChargeAsync(user, pack);
        if (charge == null)
        {
            return new AutoRechargeResult { Success = false, Error = "Auto-recharge already in progress" };
        }

        switch (charge.Outcome)
        {
            case ServiceChargeOutcome.Succeeded when !charge.CreditsGranted:
                // Paid but not granted (logged by BackofficeAutoRecharge): no success e-mail.
                return new AutoRechargeResult { Success = false, Error = "Paid, credits not granted" };

            case ServiceChargeOutcome.Abandoned:
                // An earlier charge never finished; repeating could charge twice. An admin settles it.
                await DisableAsync(user);
                return new AutoRechargeResult { Success = false, Error = "Unfinished charge expired; auto-recharge stopped" };

            case ServiceChargeOutcome.Succeeded:
                _failedAttempts.TryRemove(user.Id, out _);
                _logger.LogInformation("Auto-recharge succeeded for user {UserId}: +{Credits} credits", user.Id, pack.Quantity);
                await SendRechargeSuccessEmailAsync(user, pack.Quantity, pack.Amount, pack.Currency);
                return new AutoRechargeResult { Success = true, CreditsAdded = pack.Quantity };

            case ServiceChargeOutcome.Pending:
                _logger.LogInformation("Auto-recharge of user {UserId} is settling; credits follow with billing.payment.succeeded", user.Id);
                return new AutoRechargeResult { Success = false, Error = "Payment processing" };

            case ServiceChargeOutcome.RetryLater:
                _failedAttempts[user.Id] = DateTimeOffset.UtcNow;
                _logger.LogWarning("Auto-recharge of user {UserId}: billing API unavailable ({Reason}); repeated later with the same key",
                    user.Id, charge.Reason ?? "no answer");
                return new AutoRechargeResult { Success = false, Error = "Payment service unavailable" };

            default:
                _failedAttempts[user.Id] = DateTimeOffset.UtcNow;
                _logger.LogWarning("Auto-recharge failed for user {UserId}: {Outcome} ({Reason})", user.Id, charge.Outcome, charge.Reason);
                await SendRechargeFailedEmailAsync(user, FailureReason(charge));
                return new AutoRechargeResult { Success = false, Error = $"Payment {charge.Outcome}: {charge.Reason}" };
        }
    }

    /// <summary>The reason in the failure e-mail, worded like the direct path's.</summary>
    internal static string FailureReason(ServiceCharge charge) => charge switch
    {
        { Outcome: ServiceChargeOutcome.RequiresAction } => "Your bank asked for a confirmation we could not request automatically. Please buy credits on the billing page.",
        { Outcome: ServiceChargeOutcome.Refused, Reason: "no_payment_method" } => "No saved payment method found. Please update your payment method.",
        { Outcome: ServiceChargeOutcome.Declined } => $"Payment failed: {charge.Reason}",
        _ => "Payment was not completed. Please check your payment method.",
    };

    public async Task EnableAsync(User user, int quantity, decimal pricePer100)
    {
        user.AutoRechargeEnabled = true;
        user.AutoRechargeQuantity = quantity;
        user.AutoRechargePricePer100 = pricePer100;
        // CSPRNG, not Guid.NewGuid(). Stored in plaintext on purpose: the cancel link
        // is rebuilt from the DB every time a price-change email goes out
        // (PriceChangeMonitorService), so there is no plaintext to keep elsewhere.
        // Worst case on a DB dump is that auto-recharge gets switched off — no
        // account takeover — which is why this one isn't hashed like the reset and
        // verification tokens.
        user.AutoRechargeCancelToken = SecureTokens.Generate();
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

    private async Task SendRechargeSuccessEmailAsync(User user, int creditsAdded, decimal amount, string currency)
    {
        var baseUrl = _appOptions.BaseUrl;
        var billingUrl = $"{baseUrl}/Billing";
        // Runs outside the user's request: the language of their last sign-in, not the thread's.
        var locale = user.EmailLocale;
        var variables = EmailTemplateVariables.AutoRechargeSuccess(
            quantity: creditsAdded.ToString(),
            amount: amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            currency: currency,
            newBalance: (user.CreditsRemaining + creditsAdded).ToString(),
            billingUrl: billingUrl);

        try
        {
            await _email.SendAsync(EmailTemplateId.AutoRechargeSuccess, user.Email, locale, variables,
                new EmailSendOptions(EmailTemplateId.AutoRechargeSuccess, locale, user.Id));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send auto-recharge success email to {Email}", user.Email);
        }
    }

    private async Task SendRechargeFailedEmailAsync(User user, string reason)
    {
        var baseUrl = _appOptions.BaseUrl;
        var billingUrl = $"{baseUrl}/Billing";
        var locale = user.EmailLocale;
        var variables = EmailTemplateVariables.AutoRechargeFailed(
            quantity: user.AutoRechargeQuantity.ToString(),
            failureReason: reason,
            currentBalance: user.CreditsRemaining.ToString(),
            billingUrl: billingUrl);

        try
        {
            await _email.SendAsync(EmailTemplateId.AutoRechargeFailed, user.Email, locale, variables,
                new EmailSendOptions(EmailTemplateId.AutoRechargeFailed, locale, user.Id));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send auto-recharge failure email to {Email}", user.Email);
        }
    }
}
