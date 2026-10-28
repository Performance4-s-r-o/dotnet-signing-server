using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Email;
using Microsoft.Extensions.Options;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Services.Pricing;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>
/// <c>billing.*</c> events by <c>Modules:Billing</c>.
///
/// On: the events are the source of truth for payments made through the service.
/// <c>billing.checkout.completed</c> grants a paid purchase (the confirm page usually got there
/// first — both claim the same key), <c>billing.payment.succeeded</c> an auto-recharge that was
/// still settling, <c>billing.payment.*</c> of an auto-recharge ends its claim, and
/// <c>billing.payment_method.detached</c> switches auto-recharge off when no card is left.
///
/// </summary>
public sealed class BillingEventsHandler : IBackofficeEventHandler
{
    private readonly ApplicationDbContext _db;
    private readonly IServiceProvider _services;
    private readonly ILogger<BillingEventsHandler> _logger;

    public BillingEventsHandler(ApplicationDbContext db, IServiceProvider services, ILogger<BillingEventsHandler> logger)
    {
        _db = db;
        _services = services;
        _logger = logger;
    }

    public IReadOnlyCollection<string> Types { get; } =
    [
        BackofficeEventTypes.BillingCheckoutCompleted,
        BackofficeEventTypes.BillingPaymentSucceeded,
        BackofficeEventTypes.BillingPaymentFailed,
        BackofficeEventTypes.BillingPaymentMethodDetached,
    ];

    public Task HandleAsync(BackofficeEvent evt, CancellationToken cancellationToken) => evt.Type switch
    {
        BackofficeEventTypes.BillingCheckoutCompleted => CheckoutCompletedAsync(evt, cancellationToken),
        BackofficeEventTypes.BillingPaymentSucceeded => PaymentAsync(evt, succeeded: true, cancellationToken),
        BackofficeEventTypes.BillingPaymentFailed => FailedAsync(evt, cancellationToken),
        BackofficeEventTypes.BillingPaymentMethodDetached => PaymentMethodDetachedAsync(evt, cancellationToken),
        _ => Task.CompletedTask,
    };

    private async Task CheckoutCompletedAsync(BackofficeEvent evt, CancellationToken cancellationToken)
    {
        var purchase = BillingEventData.Purchase(evt.Data, out var reason);
        if (purchase is null)
        {
            _logger.LogInformation("[billing] {EventType} {EventId}: nothing to grant ({Reason})", evt.Type, evt.Id, reason);
            return;
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == purchase.UserId, cancellationToken);
        if (user is null)
        {
            _logger.LogWarning("[billing] {EventId}: user {UserId} not found", evt.Id, purchase.UserId);
            return;
        }
        if (user.ErasedAt != null)
        {
            // Paid after the account was erased (#217): nothing is granted to a tombstone.
            await BillingRefusals.RecordAsync(_db, BillingRefusals.CheckoutRefused, purchase.SessionId, user.Id,
                purchase.StripeCustomerId, null, purchase.AmountTotal, purchase.Currency,
                "account erased", "backoffice", _logger, cancellationToken);
            return;
        }

        // Credits, the payment record and the auto-recharge opt-in in one transaction.
        var grantedNow = await CreditGrants.TryGrantAsync(_db, purchase.SessionId, user.Id,
            purchase.Documents, "backoffice", async () =>
            {
                // customer_ref already matched the user; still never link a customer another user holds (#82).
                await StripeCustomerBinding.TryBindAsync(_db, user, purchase.StripeCustomerId, _logger, cancellationToken);
                if (purchase.AutoRecharge)
                {
                    var pricing = _services.GetRequiredService<ICreditPricingProvider>();
                    await _services.GetRequiredService<IAutoRechargeService>().EnableAsync(user, purchase.Documents, pricing.PricePer100);
                }
                _db.Payments.Add(new Payment
                {
                    UserId = user.Id,
                    StripePaymentIntentId = purchase.PaymentIntentId,
                    AmountCents = (int)(purchase.AmountTotal ?? 0),
                    Currency = (purchase.Currency ?? "").ToUpperInvariant(),
                    Status = "succeeded",
                });
            }, cancellationToken, purchase.PaymentIntentId);
        if (!grantedNow) return;
        _logger.LogInformation("[billing] {EventId}: granted {Credits} credits to user {UserId}", evt.Id, purchase.Documents, user.Id);
    }

    private async Task FailedAsync(BackofficeEvent evt, CancellationToken cancellationToken)
    {
        await NotifyPaymentFailedAsync(evt, cancellationToken);
        await PaymentAsync(evt, succeeded: false, cancellationToken);
    }

    /// <summary>
    /// Records the failed payment and tells the user (template <c>payment_failed</c>), once per
    /// payment intent: a redelivery or a retry of the event finds the record and sends nothing.
    /// No credits are touched. A direct send that fails is logged, not retried (the record
    /// stays, so no second e-mail follows).
    /// </summary>
    private async Task NotifyPaymentFailedAsync(BackofficeEvent evt, CancellationToken cancellationToken)
    {
        var failed = BillingEventData.Failed(evt.Data, out var reason);
        if (failed is null)
        {
            _logger.LogDebug("[billing] {EventId}: no failure notice ({Reason})", evt.Id, reason);
            return;
        }
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == failed.UserId, cancellationToken);
        if (user is null || user.ErasedAt != null) return;
        // An auto-recharge failure is already told to the user by the charge itself.
        var notify = failed.PaymentType != CreditGrants.AutoRechargeType;
        if (await _db.Payments.AnyAsync(p => p.StripePaymentIntentId == failed.PaymentIntentId && p.Status == "failed", cancellationToken)) return;

        var currency = (failed.Currency ?? "").ToUpperInvariant();
        _db.Payments.Add(new Payment
        {
            UserId = user.Id,
            StripePaymentIntentId = failed.PaymentIntentId,
            AmountCents = (int)(failed.Amount ?? 0),
            Currency = currency,
            Status = "failed",
        });

        var email = notify ? _services.GetService<ITemplatedEmailSender>() : null;
        if (email is null)
        {
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }
        var locale = user.EmailLocale;
        var variables = EmailTemplateVariables.PaymentFailed(
            paymentType: failed.PaymentType.Replace("_", " "),
            amount: ((failed.Amount ?? 0) / 100.0m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            currency: currency,
            failureReason: failed.FailureCode ?? "Unknown error",
            billingUrl: $"{_services.GetRequiredService<IOptions<AppOptions>>().Value.BaseUrl}/Billing");
        var options = new EmailSendOptions(EmailTemplateId.PaymentFailed, locale, user.Id);
        // Email module On: queued in the same save as the failed payment.
        var queued = email.TryEnqueue(EmailTemplateId.PaymentFailed, user.Email, locale, variables, options);
        await _db.SaveChangesAsync(cancellationToken);
        if (queued) return;
        try
        {
            await email.SendAsync(EmailTemplateId.PaymentFailed, user.Email, locale, variables, options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[billing] {EventId}: the payment failure e-mail could not be sent", evt.Id);
        }
    }

    private async Task PaymentAsync(BackofficeEvent evt, bool succeeded, CancellationToken cancellationToken)
    {
        var payment = BillingEventData.AutoRecharge(evt.Data, out var reason);
        if (payment is null)
        {
            _logger.LogDebug("[billing] {EventType} {EventId}: ignored ({Reason})", evt.Type, evt.Id, reason);
            return;
        }

        var key = CreditGrants.AutoRechargeKey(payment.PaymentIntentId);
        if (succeeded && await _db.Users.AnyAsync(u => u.Id == payment.UserId && u.ErasedAt != null, cancellationToken))
        {
            // Charged after the account was erased (#217): nothing is granted to a tombstone.
            await BillingRefusals.RecordAsync(_db, BillingRefusals.PaymentRefused, payment.PaymentIntentId, payment.UserId,
                null, null, payment.Amount, payment.Currency, "account erased", "backoffice", _logger, cancellationToken);
            succeeded = false;
        }
        else if (succeeded
            && await CreditGrants.TryGrantAsync(_db, key, payment.UserId, payment.Documents, "backoffice",
                () =>
                {
                    _db.Payments.Add(new Payment
                    {
                        UserId = payment.UserId,
                        StripePaymentIntentId = payment.PaymentIntentId,
                        AmountCents = (int)(payment.Amount ?? 0),
                        Currency = (payment.Currency ?? "").ToUpperInvariant(),
                        Status = "succeeded",
                    });
                    return Task.CompletedTask;
                }, cancellationToken, payment.PaymentIntentId))
        {
            _logger.LogInformation("[billing] {EventId}: granted {Credits} auto-recharge credits to user {UserId}",
                evt.Id, payment.Documents, payment.UserId);
        }
        else if (!succeeded)
        {
            _logger.LogWarning("[billing] {EventId}: auto-recharge of user {UserId} failed ({FailureCode})",
                evt.Id, payment.UserId, payment.FailureCode);
        }

        // This charge is over either way: the next recharge may start with a new key. Only its
        // own claim is released — a late event of an earlier charge leaves a newer one alone.
        await BackofficeAutoRecharge.ReleaseAsync(_db, payment.UserId, payment.AttemptKey, cancellationToken);
    }

    private async Task PaymentMethodDetachedAsync(BackofficeEvent evt, CancellationToken cancellationToken)
    {
        if (BillingEventData.DetachedFrom(evt.Data) is not { } userId) return;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || !user.AutoRechargeEnabled) return;

        // Throws when the service cannot say: the inbox retries.
        if (await _services.GetRequiredService<IPaymentGateway>().HasSavedCardAsync(user, cancellationToken)) return;

        await _services.GetRequiredService<IAutoRechargeService>().DisableAsync(user);
        _logger.LogInformation("[billing] {EventId}: auto-recharge disabled for user {UserId} — no card left", evt.Id, user.Id);
    }
}
