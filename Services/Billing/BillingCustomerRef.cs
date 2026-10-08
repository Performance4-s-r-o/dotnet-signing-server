namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// The billing API names a paying customer by the product's own reference. Here one user is
/// one customer: <c>user:&lt;id&gt;</c>. Pure.
/// </summary>
public static class BillingCustomerRef
{
    public const string Prefix = "user:";

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
    /// Whether a checkout session may be confirmed by <paramref name="userId"/>: its
    /// <c>userId</c> metadata, when present, and its customer reference, when the session has
    /// one, must both name the user.
    /// </summary>
    public static bool CheckoutBelongsTo(CheckoutSessionInfo session, Guid userId)
    {
        if (session.Metadata.TryGetValue("userId", out var metaUserId) && metaUserId != userId.ToString())
        {
            return false;
        }
        return session.CustomerRef is null || session.CustomerRef == For(userId);
    }
}
