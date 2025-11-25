using DotNetSigningServer.Models;

namespace DotNetSigningServer.Services;

public interface IBillingService
{
    decimal CalculateAmountForDocuments(int documentCount, decimal pricePer100);
}
