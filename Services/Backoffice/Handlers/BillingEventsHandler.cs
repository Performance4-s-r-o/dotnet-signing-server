using DotNetSigningServer.Data;
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
/// Shadow: payments still go to Stripe directly; the events are only logged and compared
/// with what the direct path recorded.
/// </summary>
public sealed class BillingEventsHandler : IBackofficeEventHandler
{
    private readonly BackofficeMode _mode;
    private readonly ApplicationDbContext _db;
    private readonly IServiceProvider _services;
    private readonly ILogger<BillingEventsHandler> _logger;

    public BillingEventsHandler(BackofficeMode mode, ApplicationDbContext db, IServiceProvider services, ILogger<BillingEventsHandler> logger)
    {
        _mode = mode;
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
        BackofficeEventTypes.BillingPaymentFailed => PaymentAsync(evt, succeeded: false, cancellationToken),
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

        if (_mode != BackofficeMode.On)
        {
            var granted = await CreditGrants.IsGrantedAsync(_db, purchase.SessionId, cancellationToken);
            _logger.LogInformation("[billing] Shadow {EventType} {EventId}: {Documents} credits for user {UserId}; granted locally: {Granted}",
                evt.Type, evt.Id, purchase.Documents, purchase.UserId, granted);
            return;
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == purchase.UserId, cancellationToken);
        if (user is null)
        {
            _logger.LogWarning("[billing] {EventId}: user {UserId} not found", evt.Id, purchase.UserId);
            return;
        }

        if (!await CreditGrants.TryGrantAsync(_db, purchase.SessionId, CreditGrants.CheckoutConfirmType, user.Id,
                purchase.Documents, "backoffice", cancellationToken))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(user.StripeCustomerId) && !string.IsNullOrWhiteSpace(purchase.StripeCustomerId))
        {
            user.StripeCustomerId = purchase.StripeCustomerId;
        }
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
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("[billing] {EventId}: granted {Credits} credits to user {UserId}", evt.Id, purchase.Documents, user.Id);
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
        if (_mode != BackofficeMode.On)
        {
            var granted = await CreditGrants.IsGrantedAsync(_db, key, cancellationToken);
            _logger.LogInformation("[billing] Shadow {EventType} {EventId}: auto-recharge of user {UserId}; granted locally: {Granted}",
                evt.Type, evt.Id, payment.UserId, granted);
            return;
        }

        if (succeeded
            && await CreditGrants.TryGrantAsync(_db, key, CreditGrants.AutoRechargeType, payment.UserId, payment.Documents, "backoffice", cancellationToken))
        {
            _db.Payments.Add(new Payment
            {
                UserId = payment.UserId,
                StripePaymentIntentId = payment.PaymentIntentId,
                AmountCents = (int)(payment.Amount ?? 0),
                Currency = (payment.Currency ?? "").ToUpperInvariant(),
                Status = "succeeded",
            });
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("[billing] {EventId}: granted {Credits} auto-recharge credits to user {UserId}",
                evt.Id, payment.Documents, payment.UserId);
        }
        else if (!succeeded)
        {
            _logger.LogWarning("[billing] {EventId}: auto-recharge of user {UserId} failed ({FailureCode})",
                evt.Id, payment.UserId, payment.FailureCode);
        }

        // The charge is over either way: the next recharge may start with a new key.
        await BackofficeAutoRecharge.ReleaseAsync(_db, payment.UserId, cancellationToken);
    }

    private async Task PaymentMethodDetachedAsync(BackofficeEvent evt, CancellationToken cancellationToken)
    {
        if (BillingEventData.DetachedFrom(evt.Data) is not { } userId) return;
        if (_mode != BackofficeMode.On)
        {
            _logger.LogInformation("[billing] Shadow {EventType} {EventId}: card removed for user {UserId}", evt.Type, evt.Id, userId);
            return;
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || !user.AutoRechargeEnabled) return;

        // Throws when the service cannot say: the inbox retries.
        if (await _services.GetRequiredService<IPaymentGateway>().HasSavedCardAsync(user, cancellationToken)) return;

        await _services.GetRequiredService<IAutoRechargeService>().DisableAsync(user);
        _logger.LogInformation("[billing] {EventId}: auto-recharge disabled for user {UserId} — no card left", evt.Id, user.Id);
    }
}
