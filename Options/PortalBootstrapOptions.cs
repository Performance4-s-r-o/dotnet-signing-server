namespace DotNetSigningServer.Options;

/// <summary>
/// Bootstrap of the portal's service account and its API token from configuration (#169).
/// Empty <see cref="Token"/> = off, nothing is created.
/// </summary>
public class PortalBootstrapOptions
{
    /// <summary>The portal's server token (<c>PORTAL_BOOTSTRAP_TOKEN</c>). Only its hash is stored.</summary>
    public string? Token { get; set; }
}
