using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace DotNetSigningServer.Services;

public class StripeCheckoutService : IStripeCheckoutService
{
    private readonly StripeOptions _options;

    public StripeCheckoutService(IOptions<StripeOptions> options)
    {
        _options = options.Value;

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            StripeConfiguration.ApiKey = _options.ApiKey;
        }
    }

    public async Task<string> CreateCheckoutSessionAsync(
        User user,
        long amountCents,
        string currency,
        string successUrl,
        string cancelUrl,
        IDictionary<string, string>? metadata = null,
        bool saveCard = false)
    {
        var taxIdCollection = _options.EnableTaxIdCollection
            ? new SessionTaxIdCollectionOptions { Enabled = true }
            : null;

        var billingAddressCollection = _options.RequireBillingAddress ? "required" : "auto";

        var sessionOptions = new SessionCreateOptions
        {
            Mode = "payment",
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            Metadata = metadata != null ? new Dictionary<string, string>(metadata) : null,
            TaxIdCollection = taxIdCollection,
            BillingAddressCollection = billingAddressCollection,
            AutomaticTax = _options.EnableAutomaticTax
                ? new SessionAutomaticTaxOptions { Enabled = true }
                : null,
            // Only save the payment method when the user has explicitly opted in
            // (e.g. checked the "save card for auto-recharge" checkbox).
            PaymentIntentData = saveCard
                ? new SessionPaymentIntentDataOptions { SetupFutureUsage = "off_session" }
                : null,
            InvoiceCreation = new SessionInvoiceCreationOptions
            {
                Enabled = true
            },
            LineItems = new List<SessionLineItemOptions>
            {
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        UnitAmount = amountCents,
                        Currency = currency,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = "Signing usage",
                            Description = "Usage-based billing for document signing"
                        }
                    }
                }
            }
        };

        // Only set one of customer or customer_email to satisfy Stripe's requirements.
        if (!string.IsNullOrWhiteSpace(user.StripeCustomerId))
        {
            sessionOptions.Customer = user.StripeCustomerId;
            sessionOptions.CustomerUpdate = new SessionCustomerUpdateOptions
            {
                Name = "auto",
                Address = "auto"
            };
        }
        else
        {
            sessionOptions.CustomerEmail = user.Email;
            sessionOptions.CustomerCreation = "if_required";
        }

        var sessionService = new SessionService();
        var session = await sessionService.CreateAsync(sessionOptions);
        return session.Url ?? string.Empty;
    }

    public async Task<Session?> GetSessionAsync(string sessionId)
    {
        var sessionService = new SessionService();
        return await sessionService.GetAsync(sessionId);
    }

    public async Task<IReadOnlyList<global::Stripe.Invoice>> GetInvoicesAsync(string customerId, int limit = 10)
    {
        var invoiceService = new InvoiceService();
        var options = new InvoiceListOptions
        {
            Customer = customerId,
            Limit = limit,
        };

        var result = await invoiceService.ListAsync(options);
        return result.Data;
    }

    public async Task<string> CreateBillingPortalSessionAsync(string customerId, string returnUrl)
    {
        var portalService = new Stripe.BillingPortal.SessionService();
        var session = await portalService.CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = customerId,
            ReturnUrl = returnUrl,
        });
        return session.Url ?? string.Empty;
    }

    public async Task<string> CreateSetupSessionAsync(
        string customerId,
        string successUrl,
        string cancelUrl,
        IDictionary<string, string>? metadata = null)
    {
        var sessionOptions = new SessionCreateOptions
        {
            Mode = "setup",
            Customer = customerId,
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            PaymentMethodTypes = new List<string> { "card" },
            Metadata = metadata != null ? new Dictionary<string, string>(metadata) : null,
            SetupIntentData = metadata != null
                ? new SessionSetupIntentDataOptions
                {
                    Metadata = new Dictionary<string, string>(metadata)
                }
                : null,
        };

        var sessionService = new SessionService();
        var session = await sessionService.CreateAsync(sessionOptions);
        return session.Url ?? string.Empty;
    }

    public async Task<(string Brand, string Last4, long ExpMonth, long ExpYear)?> GetDefaultPaymentMethodAsync(string customerId)
    {
        try
        {
            // Prefer invoice-settings default, then list card payment methods
            var customerService = new CustomerService();
            var customer = await customerService.GetAsync(customerId);
            string? defaultPmId = customer.InvoiceSettings?.DefaultPaymentMethodId;

            PaymentMethod? pm = null;
            var pmService = new PaymentMethodService();

            if (!string.IsNullOrWhiteSpace(defaultPmId))
            {
                pm = await pmService.GetAsync(defaultPmId);
            }
            else
            {
                var list = await pmService.ListAsync(new PaymentMethodListOptions
                {
                    Customer = customerId,
                    Type = "card",
                    Limit = 1,
                });
                pm = list.Data.FirstOrDefault();
            }

            if (pm?.Card == null) return null;
            return (pm.Card.Brand, pm.Card.Last4, pm.Card.ExpMonth, pm.Card.ExpYear);
        }
        catch
        {
            return null;
        }
    }
}
