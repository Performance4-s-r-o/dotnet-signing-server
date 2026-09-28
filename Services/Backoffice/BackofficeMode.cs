namespace DotNetSigningServer.Services.Backoffice;

/// <summary>
/// How far a module relies on the P4 Backoffice service.
/// </summary>
public enum BackofficeMode
{
    /// <summary>Local implementation only; the service is never contacted.</summary>
    Off = 0,

    /// <summary>Local implementation stays the source of truth; the service is called alongside and differences are logged.</summary>
    Shadow = 1,

    /// <summary>The service is the source of truth; local code is the fallback.</summary>
    On = 2,
}
