namespace DotNetSigningServer.Options;

/// <summary>
/// Bootstrap of the integrator's service account and its provisioning token from configuration
/// (#169, #131). Empty <see cref="Token"/> = off, nothing is created.
/// </summary>
public class PortalBootstrapOptions
{
    /// <summary>
    /// The provisioning token (<c>PORTAL_BOOTSTRAP_TOKEN</c>): one value, or <c>new,old</c> during a
    /// rotation (both active; a value that leaves the list is revoked). Only hashes are stored.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// IPs / CIDRs the provisioning token is accepted from (<c>PORTAL_BOOTSTRAP_ALLOWED_IPS</c>),
    /// comma-separated; empty = any.
    /// </summary>
    public string? AllowedIps { get; set; }

    /// <summary>How many values the token may hold at once.</summary>
    public const int MaxValues = 2;

    /// <summary>The configured values, trimmed, empty ones dropped.</summary>
    public IReadOnlyList<string> Values() =>
        string.IsNullOrWhiteSpace(Token)
            ? []
            : Token.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
