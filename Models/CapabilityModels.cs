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

    /// <summary>
    /// Whether this server will time-stamp at all. It has no authority of its
    /// own by design, so the answer is always no unless the caller names one —
    /// stated here so nobody infers otherwise from the endpoint existing.
    /// </summary>
    public bool TimestampRequiresCallerAuthority { get; set; } = true;
}

public class SealCapability
{
    /// <summary>True only when a certificate is configured and could be loaded.</summary>
    public bool Enabled { get; set; }

    /// <summary>Subject DN of the sealing certificate — who a reader will see.</summary>
    public string? Subject { get; set; }

    public string? Issuer { get; set; }

    public DateTimeOffset? NotBefore { get; set; }

    /// <summary>
    /// When sealing stops working. Nobody is watching this on a self-hosted
    /// install, so it is reported rather than left to be discovered.
    /// </summary>
    public DateTimeOffset? NotAfter { get; set; }

    /// <summary>Set when sealing is switched on but the certificate is unusable.</summary>
    public string? Error { get; set; }
}
