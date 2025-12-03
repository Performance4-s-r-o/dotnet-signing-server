namespace DotNetSigningServer.Options;

public class BillingOptions
{
    public decimal PricePer100 { get; set; } = 5m;
    public string Currency { get; set; } = "EUR";
    public decimal Discount300 { get; set; } = 0.05m; // 5%
    public decimal Discount500 { get; set; } = 0.10m; // 10%
    public decimal Discount1000 { get; set; } = 0.15m; // 15%
}
