using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// Which Stripe customer a user is linked to (#82). <see cref="User.StripeCustomerId"/> drives
/// the billing portal, the invoice list and the off-session auto-recharge charge, so a user
/// linked to someone else's customer would see their invoices and could charge their card.
/// A link is only ever added, never replaced, and never to a customer another user holds.
/// </summary>
public static class StripeCustomerBinding
{
    /// <summary>Whether a user other than <paramref name="userId"/> is linked to <paramref name="stripeCustomerId"/>.</summary>
    public static Task<bool> HeldByAnotherUserAsync(
        ApplicationDbContext db, Guid userId, string stripeCustomerId, CancellationToken cancellationToken = default) =>
        db.Users.AnyAsync(u => u.Id != userId && u.StripeCustomerId == stripeCustomerId, cancellationToken);

    /// <summary>
    /// Whether a direct-path checkout session (no billing API customer reference) may be
    /// confirmed for <paramref name="user"/>: <see cref="BillingCustomerRef.CheckoutBelongsTo"/>,
    /// and a user without a customer may not take one that already belongs to another user.
    /// </summary>
    public static async Task<bool> CheckoutOwnedByAsync(
        ApplicationDbContext db, CheckoutSessionInfo session, User user,
        DateTimeOffset? productTagGraceUntil = null, CancellationToken cancellationToken = default)
    {
        if (!BillingCustomerRef.CheckoutBelongsTo(session, user.Id, user.StripeCustomerId, productTagGraceUntil))
        {
            return false;
        }
        if (session.CustomerRef is null
            && string.IsNullOrWhiteSpace(user.StripeCustomerId)
            && !string.IsNullOrWhiteSpace(session.StripeCustomerId)
            && await HeldByAnotherUserAsync(db, user.Id, session.StripeCustomerId, cancellationToken))
        {
            return false;
        }
        return true;
    }

    /// <summary>Unique index on <see cref="User.StripeCustomerId"/> (blank values excluded).</summary>
    public const string UniqueIndexName = "IX_Users_StripeCustomerId";

    /// <summary>
    /// Links <paramref name="stripeCustomerId"/> to a user who has no customer yet, unless another
    /// user holds it, and saves the link at once. False when the link was refused; a user who
    /// already has a customer keeps it (true when it is the same one). The check and the write
    /// are two steps; the unique index <see cref="UniqueIndexName"/> decides a race, and losing
    /// it is a refusal, not an error. Inside a caller's transaction EF wraps the save in a
    /// savepoint, so a refusal leaves the transaction usable.
    /// </summary>
    public static async Task<bool> TryBindAsync(
        ApplicationDbContext db, User user, string? stripeCustomerId, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stripeCustomerId)) return true;
        if (!string.IsNullOrWhiteSpace(user.StripeCustomerId))
        {
            return string.Equals(user.StripeCustomerId, stripeCustomerId, StringComparison.Ordinal);
        }
        if (await HeldByAnotherUserAsync(db, user.Id, stripeCustomerId, cancellationToken))
        {
            LogRefused(logger, stripeCustomerId, user.Id);
            return false;
        }

        var previous = user.StripeCustomerId;
        user.StripeCustomerId = stripeCustomerId;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsCustomerTaken(ex))
        {
            var property = db.Entry(user).Property(u => u.StripeCustomerId);
            property.CurrentValue = previous;
            property.IsModified = false;
            LogRefused(logger, stripeCustomerId, user.Id);
            return false;
        }
        return true;
    }

    /// <summary>The save broke <see cref="UniqueIndexName"/>: another user took the customer meanwhile.</summary>
    public static bool IsCustomerTaken(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation } pg
        && pg.ConstraintName == UniqueIndexName;

    private static void LogRefused(ILogger logger, string stripeCustomerId, Guid userId) =>
        logger.LogError(
            "Billing: Stripe customer {CustomerId} is already linked to another user; not linking it to user {UserId}",
            stripeCustomerId, userId);
}
