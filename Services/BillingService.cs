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
        var baseAmount = units * pricePer100;

        decimal discount = 0m;
        if (documentCount >= 1000)
        {
            discount = _options.Discount1000;
        }
        else if (documentCount >= 500)
        {
            discount = _options.Discount500;
        }
        else if (documentCount >= 300)
        {
            discount = _options.Discount300;
        }

        if (discount > 0)
        {
            baseAmount -= baseAmount * discount;
        }

        return Math.Round(baseAmount, 2, MidpointRounding.AwayFromZero);
    }

    decimal IBillingService.CalculateAmountForDocuments(int documentCount, decimal pricePer100)
    {
        return CalculateAmountForDocuments(documentCount, pricePer100);
    }
}
