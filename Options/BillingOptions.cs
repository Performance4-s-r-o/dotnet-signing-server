namespace DotNetSigningServer.Options;

public class BillingOptions
{
    public decimal PricePer100 { get; set; } = 5m;
    public string Currency { get; set; } = "EUR";
}
