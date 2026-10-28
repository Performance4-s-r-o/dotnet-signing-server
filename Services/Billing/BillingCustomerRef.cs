namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// The billing API names a paying customer by the product's own reference. Here one user is
/// one customer: <c>user:&lt;id&gt;</c>. Pure.
/// </summary>
public static class BillingCustomerRef
{
    public const string Prefix = "user:";

    /// <summary>
    /// Metadata every checkout and card-setup session of this product carries (#80). On a Stripe
    /// account shared with other products a session that merely names a matching <c>userId</c>
    /// is not ours to confirm.
    /// </summary>
    public const string ProductMarkerKey = "product";
    public const string ProductMarker = "dotnet-signing-server";

    public static string For(Guid userId) => Prefix + userId.ToString("D");

    /// <summary>The user id of a reference this product created; false for anything else.</summary>
    public static bool TryParse(string? customerRef, out Guid userId)
    {
        userId = Guid.Empty;
        return customerRef is not null
               && customerRef.StartsWith(Prefix, StringComparison.Ordinal)
               && Guid.TryParseExact(customerRef[Prefix.Length..], "D", out userId);
    }

    /// <summary>
    /// Whether a checkout session may be confirmed by <paramref name="userId"/> (#82). Fails
    /// closed: the <c>userId</c> metadata and the product marker (#80), which every session this
    /// product starts carries, are required, and the former must name the user. Through the billing API the customer reference must
    /// name the user as well (the service owns the Stripe customer behind it). On the direct
    /// path the session's Stripe customer must be the user's own once the user has one;
    /// whether a user without one may take the session's customer is
    /// <see cref="StripeCustomerBinding"/>'s question, it needs the database.
    /// </summary>
    public static bool CheckoutBelongsTo(
        CheckoutSessionInfo session, Guid userId, string? userStripeCustomerId)
    {
        if (!NamesUser(session, userId) || !CarriesProductMarker(session))
        {
            return false;
        }
        if (session.CustomerRef is not null)
        {
            return session.CustomerRef == For(userId);
        }
        return StripeCustomerMatches(userStripeCustomerId, session.StripeCustomerId);
    }

    /// <summary>Whether the session was started by this product (<see cref="ProductMarker"/>).</summary>
    public static bool CarriesProductMarker(CheckoutSessionInfo session) =>
        session.Metadata.TryGetValue(ProductMarkerKey, out var marker)
        && string.Equals(marker, ProductMarker, StringComparison.Ordinal);

    /// <summary>Whether the session's <c>userId</c> metadata names <paramref name="userId"/>.</summary>
    public static bool NamesUser(CheckoutSessionInfo session, Guid userId) =>
        session.Metadata.TryGetValue("userId", out var metaUserId)
        && Guid.TryParse(metaUserId, out var metaId)
        && metaId == userId;

    /// <summary>
    /// Whether a Stripe object's customer may act on a user whose customer is
    /// <paramref name="userStripeCustomerId"/>: always when the user has none yet (binding is
    /// checked separately), otherwise only the very same customer. A missing customer on the
    /// object does not match a user who has one.
    /// </summary>
    public static bool StripeCustomerMatches(string? userStripeCustomerId, string? objectStripeCustomerId) =>
        string.IsNullOrWhiteSpace(userStripeCustomerId)
        || string.Equals(userStripeCustomerId, objectStripeCustomerId, StringComparison.Ordinal);
}
