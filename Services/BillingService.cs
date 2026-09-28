using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Pricing;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services;

public class BillingService : IBillingService
{
    private readonly BillingOptions _options;

    public BillingService(IOptions<BillingOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>The configured price formula (<see cref="ConfigCreditPricingProvider.CalculateAmount"/>).</summary>
    public decimal CalculateAmountForDocuments(int documentCount, decimal? pricePer100Override = null) =>
        ConfigCreditPricingProvider.CalculateAmount(documentCount, pricePer100Override ?? _options.PricePer100, _options);

    decimal IBillingService.CalculateAmountForDocuments(int documentCount, decimal pricePer100)
    {
        return CalculateAmountForDocuments(documentCount, pricePer100);
    }
}
