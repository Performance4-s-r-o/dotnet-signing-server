using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// A payment that was made but granted nothing because its Stripe customer is not the user's
/// (#82). The webhook still answers 200 (Stripe would only repeat the same event), so the
/// refusal is kept as a <see cref="WebhookEvent"/> row — <c>ProcessedAt</c> null, the reason in
/// <c>Error</c> — and logged as an error: someone paid, and a person has to settle it.
/// One row per Stripe object; repeating the refusal adds nothing.
/// </summary>
public static class BillingRefusals
{
    public const string CheckoutRefused = "checkout.refused";
    public const string PaymentRefused = "payment.refused";

    public static string EventIdFor(string type, string stripeObjectId) => $"{type}:{stripeObjectId}";

    public static async Task RecordAsync(
        ApplicationDbContext db,
        string type,
        string stripeObjectId,
        Guid userId,
        string? stripeCustomerId,
        string? userStripeCustomerId,
        long? amount,
        string? currency,
        string reason,
        string source,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        logger.LogError(
            "Billing: {Type} {StripeObjectId} for user {UserId} refused ({Reason}, via {Source}): customer {CustomerId}, the user's {UserCustomerId}, amount {Amount} {Currency}. Paid but nothing granted; settle by hand.",
            type, stripeObjectId, userId, reason, source, stripeCustomerId, userStripeCustomerId, amount, currency);

        var eventId = EventIdFor(type, stripeObjectId);
        if (await db.WebhookEvents.AnyAsync(w => w.EventId == eventId, cancellationToken)) return;

        db.WebhookEvents.Add(new WebhookEvent
        {
            EventId = eventId.Length > 128 ? eventId[..128] : eventId,
            EventType = type,
            PayloadJson = JsonSerializer.Serialize(new
            {
                stripeObjectId,
                userId,
                customer = stripeCustomerId,
                userCustomer = userStripeCustomerId,
                amount,
                currency,
                reason,
                source,
            }),
            ReceivedAt = DateTimeOffset.UtcNow,
            ProcessedAt = null,
            Error = reason.Length > 512 ? reason[..512] : reason,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (CreditGrants.IsUniqueViolation(ex))
        {
            // Recorded concurrently (webhook and the confirm page).
            CreditGrants.DetachAdded(db);
        }
    }

    /// <summary>
    /// Whether the billing service, when this product uses it, confirms that checkout
    /// <paramref name="sessionId"/> was started for <paramref name="userId"/>'s customer reference.
    /// Covers a session the service started (its own Stripe customer) whose event reaches the
    /// direct Stripe webhook after the billing module was switched off. False whenever there is
    /// no service, or it cannot say — the caller then refuses.
    /// </summary>
    public static async Task<bool> ConfirmedByBillingServiceAsync(
        BackofficeBillingClient? client, string sessionId, Guid userId, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (client is null) return false;
        try
        {
            var result = await client.GetCheckoutAsync(sessionId, cancellationToken);
            if (!result.IsSuccess) return false;
            using var doc = result.RequireJson("GET checkout");
            var root = doc.RootElement;
            var metadata = BillingJson.StringMap(root, "metadata");
            return BillingJson.String(root, "customer_ref") == BillingCustomerRef.For(userId)
                   && metadata.TryGetValue("userId", out var metaUserId)
                   && Guid.TryParse(metaUserId, out var metaId)
                   && metaId == userId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Billing: could not ask the billing service about checkout {SessionId}", sessionId);
            return false;
        }
    }
}
