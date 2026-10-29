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

    /// <summary>
    /// How long a request waits for a slot in the global, heavy or AI pool before the 503 (#94, #69).
    /// 0 = no wait.
    /// </summary>
    public int GlobalConcurrencyWaitMilliseconds { get; set; } = 2000;

    /// <summary>
    /// Pool H (#69): presign, sign, sign-pfx, seal, visual-sign, timestamp, extend, fill-pdf and
    /// PDF/A at once in the process. Null or 0 = off (the default, like the global cap: the
    /// callers' own limits must be below it first). Env <c>HEAVY_OPERATIONS_LIMIT</c>.
    /// </summary>
    public int? HeavyOperationsLimit { get; set; }

    /// <summary>Pool A (#69): AI calls at once. Null or 0 = off. Env <c>AI_OPERATIONS_LIMIT</c>.</summary>
    public int? AiOperationsLimit { get; set; }

    /// <summary>
    /// Pool R (#69): PDF pages rasterised at once (find-codes). Null = max(1, ⌊C/2⌋), the value R
    /// always had; 0 = off. Env <c>RASTER_OPERATIONS_LIMIT</c>.
    /// </summary>
    public int? RasterOperationsLimit { get; set; }

    /// <summary>
    /// How long one page waits for a slot in R before the request gets 503, ms (#69). Pages of a
    /// scan that is already charged wait longer than at the other pools. Env <c>RASTER_WAIT_MS</c>.
    /// </summary>
    public int RasterWaitMilliseconds { get; set; } = 30_000;

    /// <summary>
    /// Longest wait in a user's queue, whatever the account has stored (#69). The trusted caller
    /// is exempt: it queues fairly between its own tenants. Env <c>USER_QUEUE_MAX_SECONDS</c>.
    /// </summary>
    public int UserQueueMaxSeconds { get; set; } = 30;

    /// <summary>
    /// Requests that may wait per slot of a user's limit (#69): a user with limit 3 queues at
    /// most 6, the next one gets 429 at once. Trusted caller exempt. Env <c>USER_QUEUE_LENGTH_FACTOR</c>.
    /// </summary>
    public int UserQueueLengthFactor { get; set; } = 2;

    /// <summary>Upper bound for any stored or configured queue wait, seconds.</summary>
    public const int MaxQueueSeconds = 600;

    /// <summary>Upper bound for a configured pool size; anything above is a typo.</summary>
    public const int MaxPoolLimit = 10_000;

    /// <summary>Starting hypothesis for A (#69).</summary>
    public const int SuggestedAiLimit = 4;

    /// <summary>H starts at C, the CPUs the process sees (cgroup quota included), not 2 × C (#69).</summary>
    public static int SuggestedHeavyLimit(int processorCount) => Math.Max(1, processorCount);

    /// <summary>R, as <c>CodeScanRaster</c> sizes it: max(1, ⌊C/2⌋).</summary>
    public static int SuggestedRasterLimit(int processorCount) => Math.Max(1, processorCount / 2);

    /// <summary>Starting hypothesis for the global cap, G = H + R + A (#69). Not applied automatically.</summary>
    public static int SuggestedGlobalLimit(int processorCount) =>
        SuggestedHeavyLimit(processorCount) + SuggestedRasterLimit(processorCount) + SuggestedAiLimit;

    /// <summary>The cap in force: the configured one, or 0 (off) when unset.</summary>
    public static int EffectiveGlobalLimit(BillingOptions options, int processorCount) =>
        options.GlobalConcurrencyLimit is { } configured ? Math.Max(0, configured) : 0;

    /// <summary>The wait in a user's queue: the stored one, capped unless the caller is trusted.</summary>
    public static int EffectiveQueueSeconds(int stored, int maxSeconds, bool trustedCaller) =>
        Math.Max(0, trustedCaller ? stored : Math.Min(stored, maxSeconds));

    /// <summary>How many may wait behind a user's limit; unlimited for the trusted caller.</summary>
    public static int MaxWaiting(int limit, int factor, bool trustedCaller) =>
        trustedCaller ? int.MaxValue : (int)Math.Min(int.MaxValue, (long)Math.Max(0, limit) * Math.Max(0, factor));

    /// <summary>
    /// Retry-After after a 429 from the user's queue (#69): at least 5 s, and no sooner than the
    /// queue wait, since a request that just waited that long found nothing free. At most 60 s.
    /// </summary>
    public static int RetryAfterSeconds(int queueSeconds) => Math.Clamp(queueSeconds, 5, 60);
}
