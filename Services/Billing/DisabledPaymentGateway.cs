using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Pricing;

namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// The gateway while <c>P4Backoffice:Modules:Billing</c> is not On: a private server (which has
/// no billing pages at all) or a development host. Production refuses to start without the
/// module, so this never takes a payment.
///
/// Everything that would start or confirm a payment throws, so a misconfigured host fails
/// loudly instead of pretending. Reads answer "nothing on file" and removing cards is a no-op:
/// there is no payer anywhere to ask.
/// </summary>
public sealed class DisabledPaymentGateway : IPaymentGateway
{
    private static InvalidOperationException Off() =>
        new("Payments are not available: set P4Backoffice:Modules:Billing=On (payments go through the billing service).");

    public Task EnsureCustomerAsync(User user, CancellationToken cancellationToken = default) => throw Off();

    public Task<string> StartCheckoutAsync(
        User user, CreditPack pack, string successUrl, string cancelUrl,
        IDictionary<string, string> metadata, bool saveCard, CancellationToken cancellationToken = default) => throw Off();

    public Task<string> StartCardSetupAsync(
        User user, string successUrl, string cancelUrl,
        IDictionary<string, string> metadata, CancellationToken cancellationToken = default) => throw Off();

    public Task<CheckoutSessionInfo?> GetCheckoutAsync(string sessionId, CancellationToken cancellationToken = default) => throw Off();

    public Task<string> CreatePortalSessionAsync(User user, string returnUrl, CancellationToken cancellationToken = default) => throw Off();

    public Task<IReadOnlyList<InvoiceSummary>> ListInvoicesAsync(User user, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InvoiceSummary>>([]);

    public Task<SavedPaymentMethod?> GetSavedPaymentMethodAsync(User user, CancellationToken cancellationToken = default) =>
        Task.FromResult<SavedPaymentMethod?>(null);

    public Task<bool> HasSavedCardAsync(User user, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task DetachPaymentMethodsAsync(User user, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
