using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// A payment that was made but granted nothing because its Stripe customer is not the user's
/// (#82). The caller still answers success (a repeat would change nothing), so the
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
            // Recorded concurrently (event handler and the confirm page).
            CreditGrants.DetachAdded(db);
        }
    }
}
