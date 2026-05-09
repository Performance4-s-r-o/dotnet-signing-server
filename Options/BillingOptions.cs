namespace DotNetSigningServer.Options;

public class BillingOptions
{
    public decimal PricePer100 { get; set; } = 5m;
    public string Currency { get; set; } = "EUR";
    public decimal Discount300 { get; set; } = 0.05m; // 5%
    public decimal Discount500 { get; set; } = 0.10m; // 10%
    public decimal Discount1000 { get; set; } = 0.15m; // 15%
    public string? AttachmentDebitBypassKey { get; set; }

    /// <summary>
    /// Number of parallel slots per tier. Example: 5 means slots 1–5 cost 1×,
    /// slots 6–10 cost 2×, slots 11–15 cost 3×, etc.
    /// </summary>
    public int ConcurrencyTierSize { get; set; } = 5;

    /// <summary>
    /// Default max concurrent operations per user when User.MaxConcurrentOperations is null.
    /// </summary>
    public int ConcurrencyDefaultLimit { get; set; } = 3;

    /// <summary>
    /// Maximum tier multiplier. Above this many tiers, cost stops scaling.
    /// Example: MaxConcurrencyTier=10 caps at 10× base cost.
    /// </summary>
    public int MaxConcurrencyTier { get; set; } = 10;

    /// <summary>
    /// Global default queue timeout in seconds when User.ConcurrencyQueueTimeoutSeconds is null.
    /// 0 = reject immediately with 429 (default behaviour).
    /// </summary>
    public int ConcurrencyQueueTimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// Hard cap on total concurrent operations across all users. 0 = disabled.
    /// When exceeded, server returns 503 immediately. Set via appsettings to protect the server
    /// under unexpected load spikes without code changes.
    /// </summary>
    public int GlobalConcurrencyLimit { get; set; } = 0;
}
