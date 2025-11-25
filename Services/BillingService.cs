using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services;

public class BillingService : IBillingService
{
    private readonly BillingOptions _options;

    public BillingService(IOptions<BillingOptions> options)
    {
        _options = options.Value;
    }

    public decimal CalculateAmountForDocuments(int documentCount, decimal? pricePer100Override = null)
    {
        decimal pricePer100 = pricePer100Override ?? _options.PricePer100;
        if (documentCount <= 0 || pricePer100 <= 0)
        {
            return 0m;
        }

        int units = (int)Math.Ceiling(documentCount / 100m);
        return units * pricePer100;
    }

    decimal IBillingService.CalculateAmountForDocuments(int documentCount, decimal pricePer100)
    {
        return CalculateAmountForDocuments(documentCount, pricePer100);
    }
}
