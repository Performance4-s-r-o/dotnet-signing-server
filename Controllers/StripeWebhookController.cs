using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Billing;
using DotNetSigningServer.Services.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;

namespace DotNetSigningServer.Controllers;

[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class StripeWebhookController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly StripeOptions _stripeOptions;
    private readonly BillingOptions _billingOptions;
    private readonly IAutoRechargeService _autoRechargeService;
    private readonly DotNetSigningServer.Services.Pricing.ICreditPricingProvider _pricing;
    private readonly ITemplatedEmailSender _email;
    private readonly AppOptions _appOptions;
    private readonly ILogger<StripeWebhookController> _logger;
    private readonly IOptions<P4BackofficeProductOptions>? _backofficeOptions;

    public StripeWebhookController(
        ApplicationDbContext dbContext,
        IOptions<StripeOptions> stripeOptions,
        IOptions<BillingOptions> billingOptions,
        IAutoRechargeService autoRechargeService,
        ITemplatedEmailSender email,
        IOptions<AppOptions> appOptions,
        ILogger<StripeWebhookController> logger,
        DotNetSigningServer.Services.Pricing.ICreditPricingProvider pricing,
        IOptions<P4BackofficeProductOptions>? backofficeOptions = null)
    {
        _backofficeOptions = backofficeOptions;
        _pricing = pricing;
        _dbContext = dbContext;
        _stripeOptions = stripeOptions.Value;
        _billingOptions = billingOptions.Value;
        _autoRechargeService = autoRechargeService;
        _email = email;
        _appOptions = appOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Stripe events of the product's own Stripe account (<c>Modules:Billing</c> Off or Shadow).
    ///
    /// An event is stored once, in <c>WebhookEvents</c> under its Stripe id: <c>ProcessedAt</c>
    /// set when it was handled, null with <c>Error</c> when handling failed. Only a handled event
    /// is a duplicate; a failed one answers 500, so Stripe delivers it again (for up to three
    /// days) and the next delivery handles it anew — credits are granted once by
    /// <see cref="CreditGrants"/> however often that happens (#79). A failed event can also be
    /// resent from the Stripe dashboard.
    ///
    /// With <c>Modules:Billing</c> On, payments go through the billing service and its
    /// <c>billing.*</c> events grant credits; the endpoint then answers 200 and does nothing
    /// (a 4xx run makes Stripe disable the endpoint).
    /// </summary>
    [HttpPost("/api/webhooks/stripe")]
    public async Task<IActionResult> HandleWebhook()
    {
        if (BillingModeIsOn)
        {
            _logger.LogWarning("Stripe webhook refused: P4Backoffice:Modules:Billing is On, credits come from billing.* events");
            return Ok(new { ignored = true });
        }

        // Defence in depth behind the startup guard in Program.cs: an empty secret
        // would make ConstructEvent accept an attacker-computed signature.
        if (string.IsNullOrWhiteSpace(_stripeOptions.WebhookSecret))
        {
            _logger.LogError("Stripe webhook rejected: Stripe:WebhookSecret is not configured");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Webhook secret not configured" });
        }

        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(
                json,
                Request.Headers["Stripe-Signature"],
                _stripeOptions.WebhookSecret,
                throwOnApiVersionMismatch: false);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Stripe webhook signature validation failed");
            return BadRequest(new { error = "Invalid signature" });
        }

        // Idempotency: only an event that was handled is a duplicate. One that failed is handled again.
        var previous = await _dbContext.WebhookEvents.AsNoTracking()
            .Where(w => w.EventId == stripeEvent.Id)
            .Select(w => new { w.ProcessedAt })
            .FirstOrDefaultAsync();
        if (previous?.ProcessedAt != null)
        {
            _logger.LogDebug("Webhook event {EventId} already processed, skipping", stripeEvent.Id);
            return Ok();
        }

        _logger.LogInformation("Processing Stripe webhook: {EventType} ({EventId}){Retry}",
            stripeEvent.Type, stripeEvent.Id, previous != null ? " again after a failure" : "");

        try
        {
            await DispatchAsync(stripeEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing webhook event {EventId} ({EventType}); answering 500 so Stripe retries",
                stripeEvent.Id, stripeEvent.Type);
            await RecordFailureAsync(stripeEvent, json, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Event processing failed" });
        }

        await RecordProcessedAsync(stripeEvent, json);
        return Ok();
    }

    private bool BillingModeIsOn =>
        _backofficeOptions?.Value.ModeFor(BackofficeModule.Billing) == BackofficeMode.On;

    private Task DispatchAsync(Event stripeEvent) => stripeEvent.Type switch
    {
        "checkout.session.completed" => HandleCheckoutSessionCompletedAsync(stripeEvent),
        "payment_intent.succeeded" => HandlePaymentIntentSucceededAsync(stripeEvent),
        "payment_intent.payment_failed" => HandlePaymentIntentFailedAsync(stripeEvent),
        "payment_method.detached" => HandlePaymentMethodDetachedAsync(stripeEvent),
        _ => LogUnhandled(stripeEvent),
    };

    private Task LogUnhandled(Event stripeEvent)
    {
        _logger.LogDebug("Unhandled Stripe event type: {EventType}", stripeEvent.Type);
        return Task.CompletedTask;
    }

    /// <summary>Saves the event as handled, together with what its handler left to save (payment record, e-mail).</summary>
    private async Task RecordProcessedAsync(Event stripeEvent, string json)
    {
        var now = DateTimeOffset.UtcNow;
        var row = await _dbContext.WebhookEvents.FirstOrDefaultAsync(w => w.EventId == stripeEvent.Id);
        if (row == null)
        {
            _dbContext.WebhookEvents.Add(NewRow(stripeEvent, json, now, processedAt: now, error: null));
        }
        else
        {
            row.ProcessedAt = now;
            row.Error = null;
        }

        try
        {
            await _dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another delivery handled the same event concurrently — that's fine
            _logger.LogDebug("Concurrent webhook processing for {EventId}, ignoring duplicate", stripeEvent.Id);
        }
    }

    /// <summary>
    /// Keeps the failure (<c>ProcessedAt</c> null, the error) and nothing the failed handler left
    /// in the context. Best effort: the 500 makes Stripe retry even when this cannot be saved.
    /// </summary>
    private async Task RecordFailureAsync(Event stripeEvent, string json, Exception failure)
    {
        var error = failure.Message.Length > 512 ? failure.Message[..512] : failure.Message;
        try
        {
            _dbContext.ChangeTracker.Clear();
            var row = await _dbContext.WebhookEvents.FirstOrDefaultAsync(w => w.EventId == stripeEvent.Id);
            if (row == null)
            {
                _dbContext.WebhookEvents.Add(NewRow(stripeEvent, json, DateTimeOffset.UtcNow, processedAt: null, error));
            }
            else if (row.ProcessedAt == null)
            {
                row.Error = error;
            }
            await _dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _dbContext.ChangeTracker.Clear();
            _logger.LogWarning(ex, "Could not record the failure of webhook event {EventId}", stripeEvent.Id);
        }
    }

    private static WebhookEvent NewRow(Event stripeEvent, string json, DateTimeOffset receivedAt, DateTimeOffset? processedAt, string? error) => new()
    {
        EventId = stripeEvent.Id,
        EventType = stripeEvent.Type,
        PayloadJson = json.Length > 4000 ? json[..4000] : json,
        ReceivedAt = receivedAt,
        ProcessedAt = processedAt,
        Error = error,
    };

    /// <summary>
    /// Safety net for checkout credit granting. The primary path is ConfirmCheckout (client redirect),
    /// but if the user closes the browser before redirect, this ensures credits are still granted.
    /// </summary>
    private async Task HandleCheckoutSessionCompletedAsync(Event stripeEvent)
    {
        var session = stripeEvent.Data.Object as Stripe.Checkout.Session;
        if (session == null)
        {
            _logger.LogWarning("checkout.session.completed: could not deserialize session");
            return;
        }

        if (session.PaymentStatus != "paid")
        {
            _logger.LogDebug("checkout.session.completed: payment status is {Status}, skipping", session.PaymentStatus);
            return;
        }

        // Check if ConfirmCheckout already processed this session
        if (await CreditGrants.IsGrantedAsync(_dbContext, session.Id, HttpContext?.RequestAborted ?? default))
        {
            _logger.LogDebug("checkout.session.completed: session {SessionId} already confirmed via redirect", session.Id);
            return;
        }

        if (!session.Metadata.TryGetValue("userId", out var userIdStr) ||
            !Guid.TryParse(userIdStr, out var userId))
        {
            _logger.LogWarning("checkout.session.completed: missing or invalid userId in metadata");
            return;
        }

        if (!session.Metadata.TryGetValue("documents", out var documentsStr) ||
            !int.TryParse(documentsStr, out var documents) ||
            documents <= 0)
        {
            _logger.LogWarning("checkout.session.completed: missing or invalid documents in metadata");
            return;
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            _logger.LogWarning("checkout.session.completed: user {UserId} not found", userId);
            return;
        }

        // metadata.userId alone is not enough (#82): the session's customer must be the user's,
        // or one no other user holds when the user has none yet. Otherwise nothing is granted.
        var sessionInfo = new CheckoutSessionInfo(
            session.Id,
            session.Mode,
            session.Status,
            session.PaymentStatus,
            new Dictionary<string, string>(session.Metadata ?? new Dictionary<string, string>()),
            session.CustomerId,
            CustomerRef: null,
            Created: session.Created == default ? null : new DateTimeOffset(DateTime.SpecifyKind(session.Created, DateTimeKind.Utc)));
        var ownedDirectly = await StripeCustomerBinding.CheckoutOwnedByAsync(
            _dbContext, sessionInfo, user, _billingOptions.ProductTagGraceUntil);
        // A session the billing service started uses the service's customer; when the service
        // is in use, it can vouch for the session. It never decides the user's own link.
        var confirmedByService = !ownedDirectly && await BillingRefusals.ConfirmedByBillingServiceAsync(
            HttpContext?.RequestServices?.GetService<BackofficeBillingClient>(), session.Id, userId, _logger);
        if (!ownedDirectly && !confirmedByService)
        {
            await BillingRefusals.RecordAsync(_dbContext, BillingRefusals.CheckoutRefused, session.Id, userId,
                session.CustomerId, user.StripeCustomerId, session.AmountTotal, session.Currency,
                "session customer is not the user's", "stripe-webhook", _logger);
            return;
        }

        // Credits, the customer link, the auto-recharge opt-in and the payment record in one
        // transaction under the permanent grant key shared with ConfirmCheckout and the billing
        // events: a failure leaves nothing behind (the retry grants), a repeat grants nothing.
        var granted = await CreditGrants.TryGrantAsync(_dbContext, session.Id, CreditGrants.CheckoutConfirmType, user.Id,
            documents, "stripe-webhook", async () =>
            {
                if (!confirmedByService)
                {
                    await StripeCustomerBinding.TryBindAsync(_dbContext, user, session.CustomerId, _logger);
                }

                // Enable auto-recharge if opted in during checkout
                if (sessionInfo.Metadata.TryGetValue("autoRecharge", out var autoRechargeValue)
                    && bool.TryParse(autoRechargeValue, out var autoRecharge)
                    && autoRecharge)
                {
                    // Enrolled at the current price of 100 credits, as in BillingController.
                    await _autoRechargeService.EnableAsync(user, documents, _pricing.PricePer100);
                }

                _dbContext.Payments.Add(new Payment
                {
                    UserId = userId,
                    StripePaymentIntentId = session.PaymentIntentId,
                    AmountCents = (int)(session.AmountTotal ?? 0),
                    Currency = session.Currency ?? _billingOptions.Currency,
                    Status = "succeeded"
                });
            }, HttpContext?.RequestAborted ?? default, session.PaymentIntentId);
        if (!granted)
        {
            _logger.LogDebug("checkout.session.completed: already processed by ConfirmCheckout for {SessionId}", session.Id);
            return;
        }

        _logger.LogInformation("checkout.session.completed: granted {Credits} credits to user {UserId} via webhook",
            documents, userId);
    }

    /// <summary>
    /// Safety net for auto-recharge payments. The primary path is AutoRechargeService (synchronous),
    /// but if it fails to record or an edge case occurs, the webhook ensures credits are granted.
    /// </summary>
    private async Task HandlePaymentIntentSucceededAsync(Event stripeEvent)
    {
        var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
        if (paymentIntent == null)
        {
            _logger.LogWarning("payment_intent.succeeded: could not deserialize payment intent");
            return;
        }

        // Only process auto_recharge payments; checkout payments are handled by checkout.session.completed
        if (!paymentIntent.Metadata.TryGetValue("type", out var type) || type != "auto_recharge")
        {
            _logger.LogDebug("payment_intent.succeeded: not auto_recharge (type={Type}), skipping", type);
            return;
        }

        // Check if AutoRechargeService already recorded this
        var autoRechargeKey = CreditGrants.AutoRechargeKey(paymentIntent.Id);
        if (await CreditGrants.IsGrantedAsync(_dbContext, autoRechargeKey, HttpContext?.RequestAborted ?? default))
        {
            _logger.LogDebug("payment_intent.succeeded: auto-recharge {PaymentIntentId} already recorded", paymentIntent.Id);
            return;
        }

        if (!paymentIntent.Metadata.TryGetValue("userId", out var userIdStr) ||
            !Guid.TryParse(userIdStr, out var userId))
        {
            _logger.LogWarning("payment_intent.succeeded: missing userId in auto_recharge metadata");
            return;
        }

        if (!paymentIntent.Metadata.TryGetValue("documents", out var documentsStr) ||
            !int.TryParse(documentsStr, out var documents) ||
            documents <= 0)
        {
            _logger.LogWarning("payment_intent.succeeded: missing documents in auto_recharge metadata");
            return;
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            _logger.LogWarning("payment_intent.succeeded: user {UserId} not found", userId);
            return;
        }

        // Auto-recharge charges the user's own customer; a charge of any other grants nothing (#82).
        if (!BillingCustomerRef.ChargeBelongsTo(user.StripeCustomerId, paymentIntent.CustomerId))
        {
            await BillingRefusals.RecordAsync(_dbContext, BillingRefusals.PaymentRefused, paymentIntent.Id, userId,
                paymentIntent.CustomerId, user.StripeCustomerId, paymentIntent.Amount, paymentIntent.Currency,
                "charge customer is not the user's", "stripe-webhook", _logger);
            return;
        }

        // Credits and the payment record in one transaction under the key AutoRechargeService uses.
        var granted = await CreditGrants.TryGrantAsync(_dbContext, autoRechargeKey, CreditGrants.AutoRechargeType, user.Id,
            documents, "stripe-webhook", () =>
            {
                _dbContext.Payments.Add(new Payment
                {
                    UserId = userId,
                    StripePaymentIntentId = paymentIntent.Id,
                    AmountCents = (int)paymentIntent.Amount,
                    Currency = paymentIntent.Currency ?? _billingOptions.Currency,
                    Status = "succeeded"
                });
                return Task.CompletedTask;
            }, HttpContext?.RequestAborted ?? default, paymentIntent.Id);
        if (!granted)
        {
            _logger.LogDebug("payment_intent.succeeded: auto-recharge {PaymentIntentId} already recorded by service", paymentIntent.Id);
            return;
        }

        _logger.LogInformation("payment_intent.succeeded: granted {Credits} auto-recharge credits to user {UserId} via webhook",
            documents, userId);
    }

    /// <summary>
    /// Notifies the user when an off-session payment fails (e.g. auto-recharge card declined).
    /// </summary>
    private async Task HandlePaymentIntentFailedAsync(Event stripeEvent)
    {
        var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
        if (paymentIntent == null)
        {
            _logger.LogWarning("payment_intent.payment_failed: could not deserialize payment intent");
            return;
        }

        if (!paymentIntent.Metadata.TryGetValue("userId", out var userIdStr) ||
            !Guid.TryParse(userIdStr, out var userId))
        {
            _logger.LogDebug("payment_intent.payment_failed: no userId in metadata, skipping");
            return;
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            return;
        }

        // Not the user's customer: neither a payment record nor an e-mail for them (#82).
        if (!BillingCustomerRef.ChargeBelongsTo(user.StripeCustomerId, paymentIntent.CustomerId))
        {
            _logger.LogWarning(
                "payment_intent.payment_failed: {PaymentIntentId} names user {UserId}, but its customer {CustomerId} is not theirs; ignored",
                paymentIntent.Id, userId, paymentIntent.CustomerId);
            return;
        }

        var failureMessage = paymentIntent.LastPaymentError?.Message ?? "Unknown error";
        _logger.LogWarning("Payment failed for user {UserId}: {Reason}", userId, failureMessage);

        // Record failed payment
        _dbContext.Payments.Add(new Payment
        {
            UserId = userId,
            StripePaymentIntentId = paymentIntent.Id,
            AmountCents = (int)paymentIntent.Amount,
            Currency = paymentIntent.Currency ?? _billingOptions.Currency,
            Status = "failed"
        });

        var paymentType = paymentIntent.Metadata.TryGetValue("type", out var t) ? t : "purchase";
        var baseUrl = _appOptions.BaseUrl;
        // A webhook from Stripe, not the user's request: the language of their last sign-in.
        var locale = user.EmailLocale;
        var variables = EmailTemplateVariables.PaymentFailed(
            paymentType: paymentType.Replace("_", " "),
            amount: (paymentIntent.Amount / 100.0m).ToString("0.00"),
            currency: paymentIntent.Currency?.ToUpperInvariant() ?? _billingOptions.Currency,
            failureReason: failureMessage,
            billingUrl: $"{baseUrl}/Billing");

        var emailOptions = new EmailSendOptions(EmailTemplateId.PaymentFailed, locale, user.Id);
        // Email module On: queued with the failed payment and the webhook record (saved by the caller).
        if (_email.TryEnqueue(EmailTemplateId.PaymentFailed, user.Email, locale, variables, emailOptions))
        {
            return;
        }

        try
        {
            await _email.SendAsync(EmailTemplateId.PaymentFailed, user.Email, locale, variables, emailOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send payment failure email to {Email}", user.Email);
        }
    }

    /// <summary>
    /// When a customer's last saved payment method is removed, auto-recharge can no longer run —
    /// disable it so the user isn't left in a state that only fails at runtime.
    /// </summary>
    private async Task HandlePaymentMethodDetachedAsync(Event stripeEvent)
    {
        var pm = stripeEvent.Data.Object as PaymentMethod;
        if (pm == null)
        {
            _logger.LogWarning("payment_method.detached: could not deserialize payment method");
            return;
        }

        // After detach, pm.CustomerId is null. The previous customer is in previous_attributes.customer.
        string? customerId = pm.CustomerId;
        if (string.IsNullOrWhiteSpace(customerId) && stripeEvent.Data.PreviousAttributes != null)
        {
            try
            {
                var prevJson = Newtonsoft.Json.JsonConvert.SerializeObject(stripeEvent.Data.PreviousAttributes);
                using var doc = System.Text.Json.JsonDocument.Parse(prevJson);
                if (doc.RootElement.TryGetProperty("customer", out System.Text.Json.JsonElement cust)
                    && cust.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    customerId = cust.GetString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "payment_method.detached: failed to parse previous_attributes");
            }
        }

        if (string.IsNullOrWhiteSpace(customerId))
        {
            _logger.LogDebug("payment_method.detached: no customer id resolved, skipping");
            return;
        }

        // Are there any saved payment methods left for this customer?
        var pmService = new PaymentMethodService();
        var remaining = await pmService.ListAsync(new PaymentMethodListOptions
        {
            Customer = customerId,
            Limit = 1,
        });
        if (remaining.Data.Count > 0)
        {
            return;
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.StripeCustomerId == customerId);
        if (user == null || !user.AutoRechargeEnabled)
        {
            return;
        }

        await _autoRechargeService.DisableAsync(user);
        _logger.LogInformation("payment_method.detached: auto-recharge disabled for user {UserId} — no payment methods remaining",
            user.Id);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // PostgreSQL unique violation = 23505
        return ex.InnerException?.Message.Contains("23505") == true
            || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true;
    }
}
