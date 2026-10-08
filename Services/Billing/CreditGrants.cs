using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// Grants purchased credits exactly once, keyed like the direct Stripe path does
/// (<c>checkout.confirm</c> + session id, <c>auto_recharge_&lt;payment intent&gt;</c>), so the
/// confirm page, the product's Stripe webhook and the <c>billing.*</c> events never grant the
/// same payment twice, whichever comes first.
/// </summary>
public static class CreditGrants
{
    public const string CheckoutConfirmType = "checkout.confirm";
    public const string AutoRechargeType = "auto_recharge";

    public static string AutoRechargeKey(string paymentIntentId) => $"auto_recharge_{paymentIntentId}";

    /// <summary>
    /// Claims <paramref name="eventId"/> and adds <paramref name="documents"/> credits. False when
    /// the payment was already granted (the claim exists). The user row is updated atomically;
    /// the caller saves anything else it adds (payment record) afterwards.
    /// </summary>
    public static async Task<bool> TryGrantAsync(
        ApplicationDbContext db, string eventId, string eventType, Guid userId, int documents, string source,
        CancellationToken cancellationToken)
    {
        db.WebhookEvents.Add(new WebhookEvent
        {
            EventId = eventId,
            EventType = eventType,
            PayloadJson = $"{{\"documents\":{documents},\"userId\":\"{userId}\",\"source\":\"{source}\"}}",
            ReceivedAt = DateTimeOffset.UtcNow,
            ProcessedAt = DateTimeOffset.UtcNow,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            DetachAdded(db);
            return false;
        }

        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Users\" SET \"CreditsRemaining\" = \"CreditsRemaining\" + {0} WHERE \"Id\" = {1}",
            [documents, userId], cancellationToken);
        return true;
    }

    /// <summary>Whether <paramref name="eventId"/> was already claimed (for Shadow comparisons).</summary>
    public static Task<bool> IsGrantedAsync(ApplicationDbContext db, string eventId, CancellationToken cancellationToken) =>
        db.WebhookEvents.AnyAsync(w => w.EventId == eventId, cancellationToken);

    /// <summary>PostgreSQL unique violation (23505), as the controllers detect it.</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("23505") == true
        || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Forgets the rows that failed to insert, so the context can be saved again.</summary>
    internal static void DetachAdded(ApplicationDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<WebhookEvent>().Where(e => e.State == EntityState.Added).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }
}
