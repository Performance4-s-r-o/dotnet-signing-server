namespace DotNetSigningServer.Options;

public class StripeOptions
{
    public string? ApiKey { get; set; }
    public string? WebhookSecret { get; set; }
    public string Currency { get; set; } = "EUR";
    public bool EnableTaxIdCollection { get; set; } = true;
    public bool RequireBillingAddress { get; set; } = true;
    public bool EnableAutomaticTax { get; set; } = true;
}
