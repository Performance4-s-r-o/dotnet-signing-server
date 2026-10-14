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
}
