namespace DotNetSigningServer.Options;

/// <summary>Paid API operations (#77, #82). Section <c>Operations</c>.</summary>
public class OperationsOptions
{
    /// <summary>A running attempt not renewed by then is presumed dead and its credits come back.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How often a live attempt pushes its lease forward. Well under <see cref="Lease"/>.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a completed result can be fetched again. The portal holds a reservation 15 min
    /// (utilities) or 35 min (two-step signature) and retries within that.
    /// </summary>
    public TimeSpan ResultRetention { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Results larger than this (serialised JSON) are delivered but not kept for replay: the
    /// operation completes without a stored result and a retry gets 410. Env
    /// <c>OPERATION_MAX_STORED_RESULT_BYTES</c>.
    /// </summary>
    public long MaxStoredResultBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>
    /// Deadline of the quick paid operations (sign, sign-pfx, timestamp, visual-sign, seal,
    /// attachment), in seconds (#72). Under Cloudflare's 100 s (the portal's own wait for post-sign is 30 s, shorter: aligning it is the portal's task); on
    /// expiry the work is cancelled and the operation fails. Env <c>OPERATION_DEADLINE_SIGN_SECONDS</c>.
    /// </summary>
    public int SignDeadlineSeconds { get; set; } = 45;

    /// <summary>fill-pdf and PDF/A conversion. Env <c>OPERATION_DEADLINE_DOCUMENT_SECONDS</c>.</summary>
    public int DocumentDeadlineSeconds { get; set; } = 60;

    /// <summary>find-codes, extend-signature and the AI endpoints. Env <c>OPERATION_DEADLINE_HEAVY_SECONDS</c>.</summary>
    public int HeavyDeadlineSeconds { get; set; } = 75;

    /// <summary>Cloudflare answers 524 after 100 s; a deadline at or above it would never be seen.</summary>
    public const int MaxDeadlineSeconds = 95;

    /// <summary>The deadline for an operation name as passed to <c>RunOperationAsync</c>.</summary>
    public TimeSpan DeadlineFor(string operation) => TimeSpan.FromSeconds(operation switch
    {
        "fill-pdf" or "pdfa" => DocumentDeadlineSeconds,
        "find-codes" or "extend-signature" or "ai-detect-fields" or "ai-extract-data" => HeavyDeadlineSeconds,
        _ => SignDeadlineSeconds,
    });

    /// <summary>
    /// Operations of one user that outlived their deadline and still run (iText cannot be
    /// interrupted). Above this a new paid operation is refused with 429. Config only
    /// (<c>Operations:MaxAbandonedPerUser</c>).
    /// </summary>
    public int MaxAbandonedPerUser { get; set; } = 2;

    /// <summary>The same for the whole process; above it 503. <c>Operations:MaxAbandonedTotal</c>.</summary>
    public int MaxAbandonedTotal { get; set; } = 16;
}
