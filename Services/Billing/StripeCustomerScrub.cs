using Stripe;

namespace DotNetSigningServer.Services.Billing;

/// <summary>What the erasure did to the user's Stripe customer (#217, D-3).</summary>
/// <param name="CustomerFound">The customer exists in Stripe and was scrubbed (it is kept, with its invoices).</param>
/// <param name="SubscriptionsCanceled">Subscriptions this erasure ended, including ones a failed earlier attempt ended.</param>
public sealed record StripeCustomerScrubResult(bool CustomerFound, int SubscriptionsCanceled)
{
    public static readonly StripeCustomerScrubResult NoCustomer = new(false, 0);
}

/// <summary>
/// The Stripe side of an account erasure when this product talks to Stripe itself
/// (<c>Modules:Billing</c> not On) — the same rule as the billing service's erasure (p4-backoffice
/// BO-4, <c>src/modules/billing/erasure.ts</c>): the customer and its invoices stay (accounting,
/// 10 years), but e-mail, name, phone, address, shipping, description and every metadata key are
/// unset, live subscriptions end at once without a new invoice or proration, and the cards go.
/// Every step can be repeated: an unset field stays unset, an ended subscription is no longer live.
/// Pure helpers here; the calls are in <see cref="StripePaymentGateway"/>.
/// </summary>
public static class StripeCustomerScrub
{
    /// <summary>Comment on a subscription ended by an erasure (read back on a retry to count it).</summary>
    public const string CancelCommentPrefix = "p4_erasure";

    public static string CancelComment(string erasureKey) => $"{CancelCommentPrefix}:{erasureKey}";

    /// <summary>Stripe idempotency key of ending one subscription for one erasure.</summary>
    public static string CancelIdempotencyKey(string erasureKey, string subscriptionId) =>
        $"erasure:{erasureKey}:subscription:{subscriptionId}:cancel";

    /// <summary>Anything not already over still has to end.</summary>
    public static bool IsLive(string? status) => status is not ("canceled" or "incomplete_expired");

    /// <summary>
    /// The update that unsets everything personal. Stripe unsets a field sent as an empty string;
    /// <c>address</c> and <c>shipping</c> are objects in the SDK, so their empty value goes as an
    /// extra parameter. Metadata keys are unset one by one (all keys the customer has).
    /// </summary>
    public static CustomerUpdateOptions UpdateOptions(IEnumerable<string>? metadataKeys)
    {
        var options = new CustomerUpdateOptions
        {
            Email = "",
            Name = "",
            Phone = "",
            Description = "",
            Metadata = (metadataKeys ?? []).Distinct(StringComparer.Ordinal).ToDictionary(k => k, _ => "", StringComparer.Ordinal),
        };
        options.AddExtraParam("address", "");
        options.AddExtraParam("shipping", "");
        return options;
    }

    public static SubscriptionCancelOptions CancelOptions(string erasureKey) => new()
    {
        InvoiceNow = false,
        Prorate = false,
        CancellationDetails = new SubscriptionCancellationDetailsOptions { Comment = CancelComment(erasureKey) },
    };

    /// <summary>Ended earlier by this very erasure (a retry after a partial failure).</summary>
    public static bool CanceledBy(string? status, string? comment, string erasureKey) =>
        status == "canceled" && comment == CancelComment(erasureKey);
}
