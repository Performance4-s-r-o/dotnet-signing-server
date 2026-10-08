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
    /// In one transaction: claims <paramref name="eventId"/>, adds <paramref name="documents"/>
    /// credits and runs <paramref name="alongside"/> (the payment record, enabling
    /// auto-recharge, …), then saves. Either all of it happens or none. False when the payment
    /// was already granted.
    /// </summary>
    public static Task<bool> TryGrantAsync(
        ApplicationDbContext db, string eventId, string eventType, Guid userId, int documents, string source,
        Func<Task> alongside, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction != null)
        {
            return GrantInTransactionAsync(db, eventId, eventType, userId, documents, source, alongside, cancellationToken);
        }

        return db.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            // A retried attempt starts from what is in the database, not from the failed one.
            DetachAdded(db);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var granted = await GrantInTransactionAsync(db, eventId, eventType, userId, documents, source, alongside, ct);
            if (granted) await tx.CommitAsync(ct);
            return granted;
        }, cancellationToken);
    }

    private static async Task<bool> GrantInTransactionAsync(
        ApplicationDbContext db, string eventId, string eventType, Guid userId, int documents, string source,
        Func<Task> alongside, CancellationToken cancellationToken)
    {
        if (await IsGrantedAsync(db, eventId, cancellationToken)) return false;

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
            // Claimed concurrently; the transaction is aborted and rolls back on dispose.
            DetachAdded(db);
            return false;
        }

        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Users\" SET \"CreditsRemaining\" = \"CreditsRemaining\" + {0} WHERE \"Id\" = {1}",
            [documents, userId], cancellationToken);
        await alongside();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Whether <paramref name="eventId"/> was already claimed.</summary>
    public static Task<bool> IsGrantedAsync(ApplicationDbContext db, string eventId, CancellationToken cancellationToken) =>
        db.WebhookEvents.AnyAsync(w => w.EventId == eventId, cancellationToken);

    /// <summary>PostgreSQL unique violation (23505), as the controllers detect it.</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("23505") == true
        || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Forgets rows added but not saved, so the context can be saved again.</summary>
    internal static void DetachAdded(ApplicationDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }
}
