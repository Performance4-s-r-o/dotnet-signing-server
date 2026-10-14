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
        ApplicationDbContext db, CheckoutSessionInfo session, User user, CancellationToken cancellationToken = default)
    {
        if (!BillingCustomerRef.CheckoutBelongsTo(session, user.Id, user.StripeCustomerId))
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

    /// <summary>
    /// Links <paramref name="stripeCustomerId"/> to a user who has no customer yet, unless another
    /// user holds it. Does not save. False when the link was refused; a user who already has a
    /// customer keeps it (true when it is the same one).
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
            logger.LogError(
                "Billing: Stripe customer {CustomerId} is already linked to another user; not linking it to user {UserId}",
                stripeCustomerId, user.Id);
            return false;
        }
        user.StripeCustomerId = stripeCustomerId;
        return true;
    }
}
