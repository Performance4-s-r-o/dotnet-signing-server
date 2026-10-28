namespace DotNetSigningServer.Models;

/// <summary>
/// What the signing server can actually do right now, as opposed to what it has
/// endpoints for.
///
/// The caller orchestrating a signing wizard has to know this before it puts a
/// user through one — otherwise a missing certificate only surfaces on the last
/// step, after the document has been chosen, placed and confirmed.
/// </summary>
public class ServerCapabilities
{
    public SealCapability Seal { get; set; } = new();

    public AiCapability Ai { get; set; } = new();

    /// <summary>
    /// Whether this server will time-stamp at all. It has no authority of its
    /// own by design, so the answer is always no unless the caller names one —
    /// stated here so nobody infers otherwise from the endpoint existing.
    /// </summary>
    public bool TimestampRequiresCallerAuthority { get; set; } = true;
}

public class SealCapability
{
    /// <summary>Always false: the server has no seal certificate of its own; the caller supplies it. </summary>
    public bool Enabled { get; set; }

}

public class AiCapability
{
    /// <summary>False while the AI switch is off or the feature is not configured.</summary>
    public bool Enabled { get; set; }
}
