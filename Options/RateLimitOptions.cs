namespace DotNetSigningServer.Options;

/// <summary>
/// Limits of #73, section <c>RateLimits</c> (env <c>RateLimits__…</c>). The numbers are the
/// ones from the issue; they apply per minute unless the name says otherwise.
/// </summary>
public class RateLimitOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>/api requests with no valid credentials, per client IP.</summary>
    public int UnauthenticatedPerMinute { get; set; } = 30;

    /// <summary>Per API key (per authenticated user), all of /api together.</summary>
    public int PerKeyPerMinute { get; set; } = 600;

    public int TsaProbePerMinute { get; set; } = 6;
    public int InspectSignaturesPerMinute { get; set; } = 30;

    public int SignUpPerHour { get; set; } = 5;

    /// <summary>Sign-in, 2FA, forgot/reset password: per client IP per 10 minutes.</summary>
    public int AuthPer10Minutes { get; set; } = 20;

    /// <summary>Failed heavy operations (PDF_* or 5xx) per key, then a block.</summary>
    public int FailuresBeforeBlock { get; set; } = 20;
    public int FailureWindowMinutes { get; set; } = 10;
    public int BlockMinutes { get; set; } = 15;

    /// <summary>Request body caps outside the document endpoints.</summary>
    public long FormBodyLimitBytes { get; set; } = 1024 * 1024;
    public long WebhookBodyLimitBytes { get; set; } = 256 * 1024;
}
