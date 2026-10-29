namespace DotNetSigningServer.Services.Backoffice;

/// <summary>
/// Why every module is Off regardless of what the configuration asks for.
/// </summary>
public enum BackofficeDisabledReason
{
    /// <summary>Nothing overrides the configuration.</summary>
    None = 0,

    /// <summary><c>PrivateServer:Enabled=true</c> — a self-hosted installation never talks to the service (its e-mail may go through its own gateway, <c>OnPremGateway</c>).</summary>
    PrivateServer = 1,
}
