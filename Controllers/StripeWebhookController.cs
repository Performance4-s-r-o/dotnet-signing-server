using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
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
    private readonly IEmailSender _emailSender;
    private readonly AppOptions _appOptions;
    private readonly ILogger<StripeWebhookController> _logger;

    public StripeWebhookController(
        ApplicationDbContext dbContext,
        IOptions<StripeOptions> stripeOptions,
        IOptions<BillingOptions> billingOptions,
        IAutoRechargeService autoRechargeService,
        IEmailSender emailSender,
        IOptions<AppOptions> appOptions,
        ILogger<StripeWebhookController> logger)
    {
        _dbContext = dbContext;
        _stripeOptions = stripeOptions.Value;
        _billingOptions = billingOptions.Value;
        _autoRechargeService = autoRechargeService;
        _emailSender = emailSender;
        _appOptions = appOptions.Value;
        _logger = logger;
    }

    [HttpPost("/api/webhooks/stripe")]
    public async Task<IActionResult> HandleWebhook()
    {
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

        // Idempotency: skip if already processed
        var alreadyProcessed = await _dbContext.WebhookEvents.AnyAsync(
            w => w.EventId == stripeEvent.Id);
        if (alreadyProcessed)
        {
            _logger.LogDebug("Webhook event {EventId} already processed, skipping", stripeEvent.Id);
            return Ok();
        }

        _logger.LogInformation("Processing Stripe webhook: {EventType} ({EventId})", stripeEvent.Type, stripeEvent.Id);

        string? error = null;
        try
        {
            switch (stripeEvent.Type)
            {
                case "checkout.session.completed":
                    await HandleCheckoutSessionCompletedAsync(stripeEvent);
                    break;

                case "payment_intent.succeeded":
                    await HandlePaymentIntentSucceededAsync(stripeEvent);
                    break;

                case "payment_intent.payment_failed":
                    await HandlePaymentIntentFailedAsync(stripeEvent);
                    break;

                default:
                    _logger.LogDebug("Unhandled Stripe event type: {EventType}", stripeEvent.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing webhook event {EventId} ({EventType})",
                stripeEvent.Id, stripeEvent.Type);
            error = ex.Message.Length > 512 ? ex.Message[..512] : ex.Message;
        }

        // Record the event regardless of success/failure
        _dbContext.WebhookEvents.Add(new WebhookEvent
        {
            EventId = stripeEvent.Id,
            EventType = stripeEvent.Type,
            PayloadJson = json.Length > 4000 ? json[..4000] : json,
            ReceivedAt = DateTimeOffset.UtcNow,
            ProcessedAt = error == null ? DateTimeOffset.UtcNow : null,
            Error = error
        });

        try
        {
            await _dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another request processed the same event concurrently — that's fine
            _logger.LogDebug("Concurrent webhook processing for {EventId}, ignoring duplicate", stripeEvent.Id);
        }

        return Ok();
    }

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
        var alreadyGranted = await _dbContext.WebhookEvents.AnyAsync(
            w => w.EventId == session.Id && w.EventType == "checkout.confirm");
        if (alreadyGranted)
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

        // Record the session as processed (same key as ConfirmCheckout uses)
        _dbContext.WebhookEvents.Add(new WebhookEvent
        {
            EventId = session.Id,
            EventType = "checkout.confirm",
            PayloadJson = $"{{\"documents\":{documents},\"userId\":\"{userId}\",\"source\":\"webhook\"}}",
            ReceivedAt = DateTimeOffset.UtcNow,
            ProcessedAt = DateTimeOffset.UtcNow
        });

        // Atomic credit increment
        await _dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE \"Users\" SET \"CreditsRemaining\" = \"CreditsRemaining\" + {0} WHERE \"Id\" = {1}",
            documents, user.Id);

        if (string.IsNullOrWhiteSpace(user.StripeCustomerId) && !string.IsNullOrWhiteSpace(session.CustomerId))
        {
            user.StripeCustomerId = session.CustomerId;
        }

        // Enable auto-recharge if opted in during checkout
        if (session.Metadata.TryGetValue("autoRecharge", out var autoRechargeValue)
            && bool.TryParse(autoRechargeValue, out var autoRecharge)
            && autoRecharge)
        {
            await _autoRechargeService.EnableAsync(user, documents, _billingOptions.PricePer100);
        }

        // Record payment
        _dbContext.Payments.Add(new Payment
        {
            UserId = userId,
            StripePaymentIntentId = session.PaymentIntentId,
            AmountCents = (int)(session.AmountTotal ?? 0),
            Currency = session.Currency ?? _billingOptions.Currency,
            Status = "succeeded"
        });

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
        var autoRechargeKey = $"auto_recharge_{paymentIntent.Id}";
        var alreadyRecorded = await _dbContext.WebhookEvents.AnyAsync(
            w => w.EventId == autoRechargeKey);
        if (alreadyRecorded)
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

        // Record and grant credits
        _dbContext.WebhookEvents.Add(new WebhookEvent
        {
            EventId = autoRechargeKey,
            EventType = "auto_recharge",
            PayloadJson = $"{{\"documents\":{documents},\"userId\":\"{userId}\",\"source\":\"webhook\"}}",
            ReceivedAt = DateTimeOffset.UtcNow,
            ProcessedAt = DateTimeOffset.UtcNow
        });

        await _dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE \"Users\" SET \"CreditsRemaining\" = \"CreditsRemaining\" + {0} WHERE \"Id\" = {1}",
            documents, user.Id);

        _dbContext.Payments.Add(new Payment
        {
            UserId = userId,
            StripePaymentIntentId = paymentIntent.Id,
            AmountCents = (int)paymentIntent.Amount,
            Currency = paymentIntent.Currency ?? _billingOptions.Currency,
            Status = "succeeded"
        });

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

        if (!user.EmailNotificationsEnabled)
        {
            return;
        }

        var paymentType = paymentIntent.Metadata.TryGetValue("type", out var t) ? t : "purchase";
        var baseUrl = _appOptions.FqdnServerName?.TrimEnd('/') ?? "https://app.p4pdf.com";

        var body = $@"
<div style=""font-family:sans-serif;max-width:600px;margin:0 auto"">
    <h2>Payment failed</h2>
    <p>A {paymentType.Replace("_", " ")} payment of <strong>{paymentIntent.Amount / 100.0m:0.00} {paymentIntent.Currency?.ToUpper()}</strong> could not be processed.</p>
    <p><strong>Reason:</strong> {failureMessage}</p>
    <p>Please <a href=""{baseUrl}/Billing"">visit your billing page</a> to update your payment method or purchase credits manually.</p>
</div>";

        try
        {
            await _emailSender.SendAsync(
                user.Email,
                "Payment failed – action required",
                body,
                $"{baseUrl}/Account/Settings",
                isCritical: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send payment failure email to {Email}", user.Email);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // PostgreSQL unique violation = 23505
        return ex.InnerException?.Message.Contains("23505") == true
            || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true;
    }
}
