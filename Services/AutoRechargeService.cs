using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Services.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services;

public class AutoRechargeService : IAutoRechargeService
{
    public const int ThresholdCredits = 10;

    private readonly ApplicationDbContext _dbContext;
    private readonly ICreditPricingProvider _pricing;
    private readonly ITemplatedEmailSender _email;
    private readonly ILogger<AutoRechargeService> _logger;
    private readonly AppOptions _appOptions;
    // Modules:Billing=On: the charge goes through the billing API. Null = no billing service
    // (private server, development): nothing is ever charged.
    private readonly BackofficeAutoRecharge? _viaService;

    /// <summary>Tracks last failed auto-recharge attempt per user to implement cooldown.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _failedAttempts = new();
    // Per-user in-flight guard: stops two concurrent debits that both crossed the
    // threshold from each creating a charge (the service's idempotency
    // key does not cover two distinct charges). In-process only — a multi-replica
    // deployment would additionally need a DB/distributed lock.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _inFlight = new();

    public AutoRechargeService(
        ApplicationDbContext dbContext,
        ITemplatedEmailSender email,
        ILogger<AutoRechargeService> logger,
        IOptions<AppOptions> appOptions,
        ICreditPricingProvider pricing,
        BackofficeAutoRecharge? viaService = null)
    {
        _viaService = viaService;
        _pricing = pricing;
        _dbContext = dbContext;
        _email = email;
        _logger = logger;
        _appOptions = appOptions.Value;
    }

    /// <summary>Whether a recharge of the user is running in this process (the account erasure waits for it, #217).</summary>
    public static bool IsInFlight(Guid userId) => _inFlight.ContainsKey(userId);

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

            // An erased (or deactivated) account is never charged (#217); checked once the
            // in-flight slot is ours, so an erasure that started first is seen here.
            if (user.ErasedAt != null || !user.IsActive)
            {
                return new AutoRechargeResult { Success = false, Error = "Account not active" };
            }

            if (!user.AutoRechargeEnabled || user.AutoRechargeQuantity <= 0)
            {
                return new AutoRechargeResult { Success = false, Error = "Auto-recharge not enabled" };
            }

            // The billing service names the payer by the user's id (customer_ref), so the local
            // Stripe customer id is not needed to charge.
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

            if (_viaService == null)
            {
                // Cooldown, so a development host does not log this on every debit.
                _failedAttempts[userId] = DateTimeOffset.UtcNow;
                _logger.LogWarning("Auto-recharge of user {UserId} skipped: P4Backoffice:Modules:Billing is not On", userId);
                return new AutoRechargeResult { Success = false, Error = "Payments not available" };
            }

            return await ChargeViaServiceAsync(user);
        }
        finally
        {
            _inFlight.TryRemove(userId, out _);
        }
    }

    /// <summary>
    /// Only a pack of the price list in force can be charged (catalog prices only); the
    /// service picks the card and refuses without one.
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
                // Paid but not granted (logged by BackofficeAutoRecharge): no success e-mail. The claim
                // is released, so the next debit would charge the card again: stop auto-recharge
                // (and rest the cooldown) until an admin has settled the payment.
                _failedAttempts[user.Id] = DateTimeOffset.UtcNow;
                await DisableAsync(user);
                return new AutoRechargeResult { Success = false, Error = "Paid, credits not granted; auto-recharge stopped" };

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

    /// <summary>The reason in the failure e-mail.</summary>
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
            _logger.LogError(ex, "Failed to send auto-recharge success email to {Email}", LogRedaction.Email(user.Email));
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
            _logger.LogError(ex, "Failed to send auto-recharge failure email to {Email}", LogRedaction.Email(user.Email));
        }
    }
}
