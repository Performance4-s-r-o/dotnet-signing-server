using DotNetSigningServer.Models;

namespace DotNetSigningServer.Services;

public interface IStripeCheckoutService
{
    Task<string> CreateCheckoutSessionAsync(
        User user,
        long amountCents,
        string currency,
        string successUrl,
        string cancelUrl,
        IDictionary<string, string>? metadata = null);

    Task<Stripe.Checkout.Session?> GetSessionAsync(string sessionId);

    Task<IReadOnlyList<global::Stripe.Invoice>> GetInvoicesAsync(string customerId, int limit = 10);
}
