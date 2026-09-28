using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Pricing;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace DotNetSigningServer.Services;

public class StripeCheckoutService : IStripeCheckoutService
{
    private readonly StripeOptions _options;
    private readonly StripePriceResolver? _priceResolver;
    private readonly ILogger<StripeCheckoutService>? _logger;
    private readonly IStripeClient? _client;

    public StripeCheckoutService(
        IOptions<StripeOptions> options,
        StripePriceResolver priceResolver,
        ILogger<StripeCheckoutService> logger)
        : this(options, priceResolver, logger, client: null)
    {
    }

    /// <summary>Tests: <paramref name="client"/> replaces the global Stripe client for Checkout sessions.</summary>
    internal StripeCheckoutService(
        IOptions<StripeOptions> options,
        StripePriceResolver? priceResolver,
        ILogger<StripeCheckoutService>? logger,
        IStripeClient? client)
    {
        _options = options.Value;
        _priceResolver = priceResolver;
        _logger = logger;
        _client = client;

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
        var sessionOptions = BuildSessionOptions(user, InlineLineItem(amountCents, currency), successUrl, cancelUrl, metadata, saveCard);
        var session = await Sessions().CreateAsync(sessionOptions);
        return session.Url ?? string.Empty;
    }

    /// <summary>
    /// Checkout of a credit pack: one line with the pack's Stripe Price when
    /// <see cref="StripePriceResolver"/> finds a matching one (lookup key, amount, currency),
    /// otherwise — and when Stripe refuses the session with that Price, e.g. because the
    /// account has no default tax behaviour — the pack's amount inline, as before.
    /// </summary>
    public async Task<string> CreateCheckoutSessionAsync(
        User user,
        CreditPack pack,
        string successUrl,
        string cancelUrl,
        IDictionary<string, string>? metadata = null,
        bool saveCard = false)
    {
        var priceId = _priceResolver == null ? null : await _priceResolver.ResolveAsync(pack);
        if (priceId != null)
        {
            try
            {
                var withPrice = BuildSessionOptions(user, PriceLineItem(priceId), successUrl, cancelUrl, metadata, saveCard);
                var session = await Sessions().CreateAsync(withPrice);
                return session.Url ?? string.Empty;
            }
            catch (StripeException ex)
            {
                _logger?.LogWarning(ex,
                    "[pricing] Checkout with Stripe Price {PriceId} ({LookupKey}) was refused ({StripeCode}); charging {Quantity} credits inline",
                    priceId, pack.LookupKey, ex.StripeError?.Code, pack.Quantity);
            }
        }

        var inline = BuildSessionOptions(user, InlineLineItem(pack.UnitAmountMinor, pack.Currency), successUrl, cancelUrl, metadata, saveCard);
        var fallback = await Sessions().CreateAsync(inline);
        return fallback.Url ?? string.Empty;
    }

    private SessionService Sessions() => _client is null ? new SessionService() : new SessionService(_client);

    /// <summary>A line charging an existing Stripe Price once.</summary>
    internal static SessionLineItemOptions PriceLineItem(string priceId) => new()
    {
        Price = priceId,
        Quantity = 1,
    };

    /// <summary>A line with the amount inline (<c>price_data</c>), as Checkout always did.</summary>
    internal static SessionLineItemOptions InlineLineItem(long amountCents, string currency) => new()
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
    };

    internal SessionCreateOptions BuildSessionOptions(
        User user,
        SessionLineItemOptions lineItem,
        string successUrl,
        string cancelUrl,
        IDictionary<string, string>? metadata,
        bool saveCard)
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
            LineItems = new List<SessionLineItemOptions> { lineItem }
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

        return sessionOptions;
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

    public async Task<SavedPaymentMethod?> GetDefaultPaymentMethodAsync(string customerId)
    {
        try
        {
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
                // Prefer card, fall back to any other type (e.g. Link)
                var cards = await pmService.ListAsync(new PaymentMethodListOptions
                {
                    Customer = customerId,
                    Type = "card",
                    Limit = 1,
                });
                pm = cards.Data.FirstOrDefault();

                if (pm == null)
                {
                    var any = await pmService.ListAsync(new PaymentMethodListOptions
                    {
                        Customer = customerId,
                        Limit = 1,
                    });
                    pm = any.Data.FirstOrDefault();
                }
            }

            if (pm == null) return null;

            if (pm.Type == "card" && pm.Card != null)
            {
                return new SavedPaymentMethod("card", pm.Card.Brand, pm.Card.Last4, pm.Card.ExpMonth, pm.Card.ExpYear);
            }
            if (pm.Type == "link" && pm.Link != null)
            {
                return new SavedPaymentMethod("link", LinkEmail: pm.Link.Email);
            }
            return new SavedPaymentMethod(pm.Type);
        }
        catch
        {
            return null;
        }
    }
}
