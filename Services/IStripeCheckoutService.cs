using DotNetSigningServer.Models;

namespace DotNetSigningServer.Services;

public record SavedPaymentMethod(
    string Type,
    string? Brand = null,
    string? Last4 = null,
    long? ExpMonth = null,
    long? ExpYear = null,
    string? LinkEmail = null);

public interface IStripeCheckoutService
{
    Task<string> CreateCheckoutSessionAsync(
        User user,
        long amountCents,
        string currency,
        string successUrl,
        string cancelUrl,
        IDictionary<string, string>? metadata = null,
        bool saveCard = false);

    Task<Stripe.Checkout.Session?> GetSessionAsync(string sessionId);

    Task<IReadOnlyList<global::Stripe.Invoice>> GetInvoicesAsync(string customerId, int limit = 10);

    Task<string> CreateBillingPortalSessionAsync(string customerId, string returnUrl);

    Task<string> CreateSetupSessionAsync(
        string customerId,
        string successUrl,
        string cancelUrl,
        IDictionary<string, string>? metadata = null);

    Task<SavedPaymentMethod?> GetDefaultPaymentMethodAsync(string customerId);
}
