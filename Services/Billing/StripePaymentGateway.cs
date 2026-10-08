using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Pricing;
using Stripe;

namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// Stripe called directly with the product's own key — the behaviour before the billing API,
/// and the one used while <c>Modules:Billing</c> is Off or Shadow. The code is the one that
/// used to live in <c>BillingController</c> and <c>AdminController</c>, unchanged.
/// </summary>
public sealed class StripePaymentGateway : IPaymentGateway
{
    private readonly IStripeCheckoutService _checkout;
    private readonly ILogger<StripePaymentGateway> _logger;

    public StripePaymentGateway(IStripeCheckoutService checkout, ILogger<StripePaymentGateway> logger)
    {
        _checkout = checkout;
        _logger = logger;
    }

    public async Task EnsureCustomerAsync(User user, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(user.StripeCustomerId)) return;

        var customer = await new CustomerService().CreateAsync(new CustomerCreateOptions
        {
            Email = user.Email,
            Metadata = new Dictionary<string, string>
            {
                { "app_user_id", user.Id.ToString() }
            }
        }, cancellationToken: cancellationToken);
        user.StripeCustomerId = customer.Id;
    }

    public Task<string> StartCheckoutAsync(
        User user,
        CreditPack pack,
        string successUrl,
        string cancelUrl,
        IDictionary<string, string> metadata,
        bool saveCard,
        CancellationToken cancellationToken = default) =>
        _checkout.CreateCheckoutSessionAsync(user, pack, successUrl, cancelUrl, metadata, saveCard);

    public Task<string> StartCardSetupAsync(
        User user,
        string successUrl,
        string cancelUrl,
        IDictionary<string, string> metadata,
        CancellationToken cancellationToken = default) =>
        _checkout.CreateSetupSessionAsync(user.StripeCustomerId!, successUrl, cancelUrl, metadata);

    public async Task<CheckoutSessionInfo?> GetCheckoutAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _checkout.GetSessionAsync(sessionId);
        if (session == null) return null;
        return new CheckoutSessionInfo(
            session.Id,
            session.Mode,
            session.Status,
            session.PaymentStatus,
            new Dictionary<string, string>(session.Metadata ?? new Dictionary<string, string>()),
            session.CustomerId,
            CustomerRef: null);
    }

    public async Task<IReadOnlyList<InvoiceSummary>> ListInvoicesAsync(User user, int limit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(user.StripeCustomerId)) return [];
        var invoices = await _checkout.GetInvoicesAsync(user.StripeCustomerId, limit);
        return invoices
            .Select(i => new InvoiceSummary(i.Number, i.AmountDue, i.Status ?? "unknown", i.HostedInvoiceUrl, i.Created))
            .ToList();
    }

    public Task<string> CreatePortalSessionAsync(User user, string returnUrl, CancellationToken cancellationToken = default) =>
        _checkout.CreateBillingPortalSessionAsync(user.StripeCustomerId!, returnUrl);

    public async Task<SavedPaymentMethod?> GetSavedPaymentMethodAsync(User user, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(user.StripeCustomerId)
            ? null
            : await _checkout.GetDefaultPaymentMethodAsync(user.StripeCustomerId);

    public async Task<bool> HasSavedCardAsync(User user, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(user.StripeCustomerId)) return false;
        var methods = await new PaymentMethodService().ListAsync(new PaymentMethodListOptions
        {
            Customer = user.StripeCustomerId,
            Type = "card",
            Limit = 1
        }, cancellationToken: cancellationToken);
        return methods.Data.Count > 0;
    }

    public async Task DetachPaymentMethodsAsync(User user, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(user.StripeCustomerId)) return;

        var pmService = new PaymentMethodService();
        var methods = await pmService.ListAsync(new PaymentMethodListOptions
        {
            Customer = user.StripeCustomerId,
            Type = "card",
            Limit = 100,
        }, cancellationToken: cancellationToken);
        foreach (var pm in methods.Data)
        {
            try
            {
                await pmService.DetachAsync(pm.Id, cancellationToken: cancellationToken);
                _logger.LogInformation("Detached payment method {PmId} for enterprise user {UserId}", pm.Id, user.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to detach payment method {PmId} for user {UserId}", pm.Id, user.Id);
            }
        }
    }
}
