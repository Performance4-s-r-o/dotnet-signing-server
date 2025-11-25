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
        IDictionary<string, string>? metadata = null)
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
}
