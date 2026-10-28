namespace DotNetSigningServer.Services.Backoffice;

/// <summary>
/// How far a module relies on the P4 Backoffice service.
/// </summary>
public enum BackofficeMode
{
    /// <summary>Local implementation only; the service is never contacted.</summary>
    Off = 0,

    /// <summary>The service is the source of truth; local code is the fallback.</summary>
    On = 1,
}
