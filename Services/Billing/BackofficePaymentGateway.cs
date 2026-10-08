using System.Text.RegularExpressions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Pricing;

namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// Payments through the P4 Backoffice billing API (<c>Modules:Billing=On</c>). The service
/// holds the Stripe key; this product names its customer <c>user:&lt;id&gt;</c>
/// (<see cref="BillingCustomerRef"/>) and charges only catalog prices of the price list in
/// force (the pack's lookup key — Modules:Pricing must be On).
///
/// Checkout and setup sessions are hosted: the user is sent to the session's URL, as before.
/// The outcome arrives as <c>billing.*</c> events (<c>BillingEventsHandler</c>); the confirm
/// pages read the session back with <c>GET /v1/billing/checkout-sessions/{id}</c>.
/// </summary>
public sealed class BackofficePaymentGateway : IPaymentGateway
{
    private static readonly Regex CustomerLocale = new("^[a-z]{2}(-[A-Z]{2})?$", RegexOptions.CultureInvariant);

    private readonly BackofficeBillingClient _client;
    private readonly ICreditPricingProvider _pricing;
    private readonly ILogger<BackofficePaymentGateway> _logger;

    public BackofficePaymentGateway(BackofficeBillingClient client, ICreditPricingProvider pricing, ILogger<BackofficePaymentGateway> logger)
    {
        _client = client;
        _pricing = pricing;
        _logger = logger;
    }

    public async Task EnsureCustomerAsync(User user, CancellationToken cancellationToken = default)
    {
        var result = await _client.PutCustomerAsync(BillingCustomerRef.For(user.Id), CustomerBody(user), cancellationToken);
        using var doc = result.RequireJson("PUT customer");
        var stripeCustomerId = BillingJson.String(doc.RootElement, "stripe_customer_id");
        if (string.IsNullOrWhiteSpace(user.StripeCustomerId))
        {
            user.StripeCustomerId = stripeCustomerId;
        }
        else if (stripeCustomerId != null && stripeCustomerId != user.StripeCustomerId)
        {
            // The user paid before the service knew them, and their customer was not imported:
            // the service uses a new one (without the saved card). Kept for the direct path.
            _logger.LogWarning(
                "Billing: user {UserId} has a Stripe customer of their own that the service does not use; import it before relying on saved cards",
                user.Id);
        }
    }

    public async Task<string> StartCheckoutAsync(
        User user,
        CreditPack pack,
        string successUrl,
        string cancelUrl,
        IDictionary<string, string> metadata,
        bool saveCard,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            CustomerRef = BillingCustomerRef.For(user.Id),
            Customer = CustomerBody(user),
            Mode = "payment",
            Ui = "hosted",
            Items = new[] { new { LookupKey = RequireLookupKey(pack), Quantity = 1 } },
            Currency = pack.StripeCurrency,
            SaveCard = saveCard,
            // As before: every credit purchase comes with an invoice.
            Invoice = true,
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            Metadata = new Dictionary<string, string>(metadata),
            // The product never limited open checkouts; keep it that way.
            SingleOpenCheckout = false,
        };
        return HostedUrl(await _client.CreateCheckoutAsync(body, cancellationToken), "POST checkout (payment)");
    }

    public async Task<string> StartCardSetupAsync(
        User user,
        string successUrl,
        string cancelUrl,
        IDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            CustomerRef = BillingCustomerRef.For(user.Id),
            Customer = CustomerBody(user),
            Mode = "setup",
            Ui = "hosted",
            // Stripe needs a currency in setup mode too.
            Currency = _pricing.Currency.ToLowerInvariant(),
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            Metadata = new Dictionary<string, string>(metadata),
            SingleOpenCheckout = false,
        };
        return HostedUrl(await _client.CreateCheckoutAsync(body, cancellationToken), "POST checkout (setup)");
    }

    public async Task<CheckoutSessionInfo?> GetCheckoutAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var result = await _client.GetCheckoutAsync(sessionId, cancellationToken);
        if (result.Status == 404) return null;
        using var doc = result.RequireJson("GET checkout");
        var root = doc.RootElement;
        return new CheckoutSessionInfo(
            BillingJson.String(root, "id") ?? sessionId,
            BillingJson.String(root, "mode"),
            BillingJson.String(root, "status"),
            BillingJson.String(root, "payment_status"),
            BillingJson.StringMap(root, "metadata"),
            StripeCustomerId: null,
            // Null would skip the ownership check; a session without a customer belongs to nobody.
            BillingJson.String(root, "customer_ref") ?? "");
    }

    public async Task<IReadOnlyList<InvoiceSummary>> ListInvoicesAsync(User user, int limit, CancellationToken cancellationToken = default)
    {
        var result = await _client.ListInvoicesAsync(BillingCustomerRef.For(user.Id), limit, cancellationToken);
        if (result.Status == 404) return [];
        using var doc = result.RequireJson("GET invoices");
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return [];
        }
        return data.EnumerateArray()
            .Select(i => new InvoiceSummary(
                BillingJson.String(i, "number"),
                BillingJson.Long(i, "amount_due") ?? 0,
                BillingJson.String(i, "status") ?? "unknown",
                BillingJson.String(i, "hosted_invoice_url"),
                BillingJson.Date(i, "created_at") ?? default))
            .ToList();
    }

    public async Task<string> CreatePortalSessionAsync(User user, string returnUrl, CancellationToken cancellationToken = default)
    {
        var body = new { CustomerRef = BillingCustomerRef.For(user.Id), ReturnUrl = returnUrl };
        var result = await _client.CreatePortalSessionAsync(body, cancellationToken);
        using var doc = result.RequireJson("POST portal session");
        return BillingJson.String(doc.RootElement, "url") ?? string.Empty;
    }

    public async Task<SavedPaymentMethod?> GetSavedPaymentMethodAsync(User user, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadCardAsync(user, cancellationToken);
        }
        catch (BillingApiException ex)
        {
            _logger.LogWarning("Billing: card of user {UserId} not read ({Result})", user.Id, ex.Result.Describe());
            return null;
        }
    }

    /// <summary>Throws when the service cannot say (unlike the page's card), so no caller acts on a guess.</summary>
    public async Task<bool> HasSavedCardAsync(User user, CancellationToken cancellationToken = default) =>
        (await ReadCardAsync(user, cancellationToken))?.Type == "card";

    private async Task<SavedPaymentMethod?> ReadCardAsync(User user, CancellationToken cancellationToken)
    {
        var result = await _client.GetCustomerAsync(BillingCustomerRef.For(user.Id), cancellationToken);
        if (result.Status == 404) return null;
        using var doc = result.RequireJson("GET customer");
        return Card(doc.RootElement);
    }

    public async Task DetachPaymentMethodsAsync(User user, CancellationToken cancellationToken = default)
    {
        var result = await _client.DetachPaymentMethodsAsync(BillingCustomerRef.For(user.Id), cancellationToken);
        if (result.Status == 404) return;
        using var doc = result.RequireJson("DELETE payment methods");
        _logger.LogInformation("Detached {Count} payment methods for enterprise user {UserId}",
            BillingJson.Long(doc.RootElement, "detached") ?? 0, user.Id);
    }

    /// <summary>The <c>card</c> of <c>GET /v1/billing/customers/{ref}</c>; null when there is none. Pure.</summary>
    internal static SavedPaymentMethod? Card(System.Text.Json.JsonElement customer)
    {
        if (!customer.TryGetProperty("card", out var card) || card.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }
        var type = BillingJson.String(card, "type") ?? "card";
        return type == "link"
            ? new SavedPaymentMethod("link", LinkEmail: BillingJson.String(card, "link_email"))
            : new SavedPaymentMethod(type, BillingJson.String(card, "brand"), BillingJson.String(card, "last4"),
                BillingJson.Long(card, "exp_month"), BillingJson.Long(card, "exp_year"));
    }

    /// <summary>What the service sends to Stripe about the customer: e-mail and, when valid, language.</summary>
    internal static object CustomerBody(User user) => new
    {
        Email = user.Email,
        Locale = CustomerLocale.IsMatch(user.Locale ?? "") ? user.Locale : null,
    };

    internal static string RequireLookupKey(CreditPack pack) =>
        string.IsNullOrWhiteSpace(pack.LookupKey)
            ? throw new InvalidOperationException(
                $"The {pack.Quantity}-credit pack has no lookup key: payments through the service need P4Backoffice:Modules:Pricing=On")
            : pack.LookupKey;

    private static string HostedUrl(BillingApiResult result, string operation)
    {
        using var doc = result.RequireJson(operation);
        return BillingJson.String(doc.RootElement, "url") ?? string.Empty;
    }
}
