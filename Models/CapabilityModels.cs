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
    /// <summary>Always false: the server has no seal certificate of its own; the caller supplies it. Kept so clients reading this field keep working.</summary>
    public bool Enabled { get; set; }

    /// <summary>Always null since #122 (no platform seal); kept for existing clients.</summary>
    public string? Subject { get; set; }

    /// <summary>Always null since #122.</summary>
    public string? Issuer { get; set; }

    /// <summary>Always null since #122.</summary>
    public DateTimeOffset? NotBefore { get; set; }

    /// <summary>Always null since #122.</summary>
    public DateTimeOffset? NotAfter { get; set; }

    /// <summary>Always null since #122.</summary>
    public string? Error { get; set; }
}
