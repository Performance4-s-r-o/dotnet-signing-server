using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Pricing;

namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// Everything the billing pages need from the payment provider, without its types.
///
/// Two implementations, chosen at startup by <c>P4Backoffice:Modules:Billing</c>:
/// <see cref="StripePaymentGateway"/> (Off and Shadow: Stripe directly, as always) and
/// <see cref="BackofficePaymentGateway"/> (On: through the P4 Backoffice billing API).
/// Methods throw on failure; callers keep their own error handling.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>
    /// Makes sure the user has a payer at the provider and fills <see cref="User.StripeCustomerId"/>
    /// when it is empty. The caller saves the user.
    /// </summary>
    Task EnsureCustomerAsync(User user, CancellationToken cancellationToken = default);

    /// <summary>Hosted checkout of one credit pack; the URL to send the user to (empty = no URL).</summary>
    Task<string> StartCheckoutAsync(
        User user,
        CreditPack pack,
        string successUrl,
        string cancelUrl,
        IDictionary<string, string> metadata,
        bool saveCard,
        CancellationToken cancellationToken = default);

    /// <summary>Hosted checkout that only saves a card (auto-recharge without a saved card).</summary>
    Task<string> StartCardSetupAsync(
        User user,
        string successUrl,
        string cancelUrl,
        IDictionary<string, string> metadata,
        CancellationToken cancellationToken = default);

    /// <summary>The checkout session the user returned from; null when it does not exist.</summary>
    Task<CheckoutSessionInfo?> GetCheckoutAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InvoiceSummary>> ListInvoicesAsync(User user, int limit, CancellationToken cancellationToken = default);

    /// <summary>The provider's page for cards and invoices; empty = no URL.</summary>
    Task<string> CreatePortalSessionAsync(User user, string returnUrl, CancellationToken cancellationToken = default);

    /// <summary>The card (or Link account) on file; null when there is none or it cannot be read.</summary>
    Task<SavedPaymentMethod?> GetSavedPaymentMethodAsync(User user, CancellationToken cancellationToken = default);

    /// <summary>Whether a card is saved for off-session payments.</summary>
    Task<bool> HasSavedCardAsync(User user, CancellationToken cancellationToken = default);

    /// <summary>Removes every saved card (enterprise accounts are invoiced manually).</summary>
    Task DetachPaymentMethodsAsync(User user, CancellationToken cancellationToken = default);
}

/// <summary>A checkout session as the confirm pages need it.</summary>
/// <param name="Mode"><c>payment</c> or <c>setup</c>.</param>
/// <param name="Status"><c>open</c>, <c>complete</c> or <c>expired</c>.</param>
/// <param name="PaymentStatus"><c>paid</c>, <c>unpaid</c> or <c>no_payment_required</c>.</param>
/// <param name="StripeCustomerId">Known only on the direct path.</param>
/// <param name="CustomerRef">The billing API's customer reference; null on the direct path.</param>
public sealed record CheckoutSessionInfo(
    string Id,
    string? Mode,
    string? Status,
    string? PaymentStatus,
    IReadOnlyDictionary<string, string> Metadata,
    string? StripeCustomerId,
    string? CustomerRef);

/// <summary>One invoice row of the billing page.</summary>
public sealed record InvoiceSummary(
    string? Number,
    long AmountDueMinor,
    string Status,
    string? HostedInvoiceUrl,
    DateTime Created);
