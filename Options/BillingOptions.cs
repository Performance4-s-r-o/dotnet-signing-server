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
    /// Checkout sessions created before this instant (UTC) are accepted without the
    /// <c>product</c> metadata marker (#80) — the ones already open when the marker was
    /// deployed. Empty = strict. Env <c>BILLING_PRODUCT_TAG_GRACE_UNTIL</c>
    /// (<c>Billing__ProductTagGraceUntil</c>), e.g. <c>2026-10-20T12:00:00Z</c>; remove it a day later.
    /// </summary>
    public DateTimeOffset? ProductTagGraceUntil { get; set; }

    /// <summary>
    /// Credits charged per AI call (/api/ai/detect-fields, /api/ai/extract-data).
    /// These invoke an LLM — a real cost — so they must not be free for direct
    /// API customers. Mirrors the portal's own AI_EXTRACTION_CREDITS (2). The
    /// exact figure is provisional pending the platform-wide re-pricing; set via
    /// appsettings/env without a code change. 0 makes AI calls free again and
    /// must be a deliberate choice.
    /// </summary>
    public int AiCallCredits { get; set; } = 2;

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
    /// Hard cap on total concurrent operations across all users. Null (unset) or 0 = off; the
    /// default stays off until the callers' own limits are set below it
    /// (see <see cref="SuggestedGlobalLimit"/>). When exceeded after
    /// <see cref="GlobalConcurrencyWaitMilliseconds"/>, the server returns 503.
    /// </summary>
    public int? GlobalConcurrencyLimit { get; set; }

    /// <summary>How long a request waits for a global slot before the 503 (#94). 0 = no wait.</summary>
    public int GlobalConcurrencyWaitMilliseconds { get; set; } = 2000;

    /// <summary>Lowest suggested global cap; the starting hypothesis of #69 for the signing instance.</summary>
    public const int MinComputedGlobalLimit = 16;

    /// <summary>Starting hypothesis for the cap, max(16, 2/3 of the CPUs). Not applied automatically.</summary>
    public static int SuggestedGlobalLimit(int processorCount) =>
        Math.Max(MinComputedGlobalLimit, processorCount * 2 / 3);

    /// <summary>The cap in force: the configured one, or 0 (off) when unset.</summary>
    public static int EffectiveGlobalLimit(BillingOptions options, int processorCount) =>
        options.GlobalConcurrencyLimit is { } configured ? Math.Max(0, configured) : 0;
}
