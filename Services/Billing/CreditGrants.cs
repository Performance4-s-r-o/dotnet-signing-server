using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// Grants purchased credits exactly once, keyed like the direct Stripe path does
/// (checkout session id, <c>auto_recharge_&lt;payment intent&gt;</c>), so the confirm page, the
/// product's Stripe webhook and the <c>billing.*</c> events never grant the same payment twice,
/// whichever comes first and however late a redelivery arrives.
///
/// The once-only key is a <see cref="CreditGrant"/> row (#79): unique <c>GrantKey</c> and unique
/// <c>PaymentIntentId</c>, inserted with <c>ON CONFLICT DO NOTHING</c> in the same transaction
/// as the credits and never deleted. A failure anywhere rolls the claim back with the credits,
/// so a retry grants them — the claim is never left behind without the credits.
/// A <c>WebhookEvents</c> row is still written alongside for the readers of the old marker
/// (last purchase on the billing page); grants made before #79 are recognised by it too.
/// </summary>
public static class CreditGrants
{
    public const string CheckoutConfirmType = "checkout.confirm";
    public const string AutoRechargeType = "auto_recharge";

    public static string AutoRechargeKey(string paymentIntentId) => $"auto_recharge_{paymentIntentId}";

    /// <summary>
    /// In one transaction: claims <paramref name="eventId"/> (and <paramref name="paymentIntentId"/>
    /// when given), adds <paramref name="documents"/> credits and runs <paramref name="alongside"/>
    /// (the payment record, enabling auto-recharge, …), then saves. Either all of it happens or
    /// none. False when the payment was already granted.
    /// </summary>
    public static Task<bool> TryGrantAsync(
        ApplicationDbContext db, string eventId, string eventType, Guid userId, int documents, string source,
        Func<Task> alongside, CancellationToken cancellationToken, string? paymentIntentId = null)
    {
        var grant = new GrantRequest(eventId, eventType, userId, documents, source, Normalize(paymentIntentId));
        if (!db.Database.IsRelational())
        {
            // InMemory (tests): no transactions and no SQL; same rules, without the cross-instance guarantee.
            return GrantWithoutTransactionAsync(db, grant, alongside, cancellationToken);
        }

        if (db.Database.CurrentTransaction != null)
        {
            return GrantInTransactionAsync(db, grant, alongside, cancellationToken);
        }

        return db.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            // A retried attempt starts from what is in the database, not from the failed one.
            DetachAdded(db);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var granted = await GrantInTransactionAsync(db, grant, alongside, ct);
            if (granted) await tx.CommitAsync(ct);
            return granted;
        }, cancellationToken);
    }

    private sealed record GrantRequest(string Key, string EventType, Guid UserId, int Documents, string Source, string? PaymentIntentId);

    private static async Task<bool> GrantInTransactionAsync(
        ApplicationDbContext db, GrantRequest grant, Func<Task> alongside, CancellationToken cancellationToken)
    {
        if (await IsGrantedAsync(db, grant.Key, cancellationToken)) return false;

        // Blocks on a concurrent uncommitted claim of the same key and then inserts nothing.
        var t = SqlNames.Of<CreditGrant>(db);
        var columns = string.Join(", ", new[]
        {
            nameof(CreditGrant.Id), nameof(CreditGrant.GrantKey), nameof(CreditGrant.PaymentIntentId), nameof(CreditGrant.UserId),
            nameof(CreditGrant.Credits), nameof(CreditGrant.Source), nameof(CreditGrant.GrantedAt),
        }.Select(c => t[c]));
        // Identifiers from the EF model; every value is a parameter. An unknown payment intent is
        // the literal NULL, not an untyped DBNull parameter (Npgsql cannot type a bare DBNull).
        var paymentIntent = grant.PaymentIntentId is null ? "NULL" : "{6}";
        var insert = $"INSERT INTO {t.Table} ({columns}) VALUES ({{0}}, {{1}}, {paymentIntent}, {{2}}, {{3}}, {{4}}, {{5}}) ON CONFLICT DO NOTHING";
        object[] values = grant.PaymentIntentId is null
            ? [Guid.NewGuid(), grant.Key, grant.UserId, grant.Documents, Truncate(grant.Source, 32), DateTimeOffset.UtcNow]
            : [Guid.NewGuid(), grant.Key, grant.UserId, grant.Documents, Truncate(grant.Source, 32), DateTimeOffset.UtcNow, grant.PaymentIntentId];
        var claimed = await db.Database.ExecuteSqlRawAsync(insert, values, cancellationToken);
        if (claimed == 0) return false;

        db.WebhookEvents.Add(Marker(grant));
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
            [grant.Documents, grant.UserId], cancellationToken);
        await alongside();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static async Task<bool> GrantWithoutTransactionAsync(
        ApplicationDbContext db, GrantRequest grant, Func<Task> alongside, CancellationToken cancellationToken)
    {
        if (await IsGrantedAsync(db, grant.Key, cancellationToken)) return false;
        if (grant.PaymentIntentId != null
            && await db.CreditGrants.AnyAsync(g => g.PaymentIntentId == grant.PaymentIntentId, cancellationToken))
        {
            return false;
        }

        // The side effects first: they may save on their own (the customer link does), and a
        // failure among them must leave the payment ungranted, as the rolled-back transaction does.
        await alongside();
        db.CreditGrants.Add(new CreditGrant
        {
            GrantKey = grant.Key,
            PaymentIntentId = grant.PaymentIntentId,
            UserId = grant.UserId,
            Credits = grant.Documents,
            Source = Truncate(grant.Source, 32),
            GrantedAt = DateTimeOffset.UtcNow,
        });
        db.WebhookEvents.Add(Marker(grant));
        var user = await db.Users.FindAsync([grant.UserId], cancellationToken);
        if (user != null) user.CreditsRemaining += grant.Documents;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static WebhookEvent Marker(GrantRequest grant) => new()
    {
        EventId = grant.Key,
        EventType = grant.EventType,
        PayloadJson = $"{{\"documents\":{grant.Documents},\"userId\":\"{grant.UserId}\",\"source\":\"{grant.Source}\"}}",
        ReceivedAt = DateTimeOffset.UtcNow,
        ProcessedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Whether <paramref name="eventId"/> was already granted (or claimed by a grant made before #79).</summary>
    public static async Task<bool> IsGrantedAsync(ApplicationDbContext db, string eventId, CancellationToken cancellationToken) =>
        await db.CreditGrants.AnyAsync(g => g.GrantKey == eventId, cancellationToken)
        || await db.WebhookEvents.AnyAsync(w => w.EventId == eventId, cancellationToken);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : Truncate(value.Trim(), 128);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

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
