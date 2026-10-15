namespace DotNetSigningServer.Options;

/// <summary>Section <c>Auth</c> (#80): step-up re-authentication and the 2FA code.</summary>
public class SessionSecurityOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// How recent the last password or 2FA confirmation of a cookie session must be before it
    /// may create an API token or change billing. Env <c>Auth__ReauthMinutes</c>.
    /// </summary>
    public int ReauthMinutes { get; set; } = 15;

    /// <summary>Wrong codes after which an e-mailed code is void and has to be requested again.</summary>
    public int OtpMaxFailedAttempts { get; set; } = 5;

    public TimeSpan ReauthWindow => TimeSpan.FromMinutes(ReauthMinutes > 0 ? ReauthMinutes : 15);
}
