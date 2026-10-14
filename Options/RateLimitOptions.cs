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

    /// <summary>
    /// Refused inputs (400, 413) on the heavy paths per key within <see cref="FailureWindowMinutes"/>,
    /// then a block of <see cref="BlockMinutes"/>. **0 = off, the default.** The portal calls this
    /// server with one account for every tenant: switched on without that account in
    /// <see cref="ExemptUserIds"/>, twenty bad PDFs from one tenant would pause signing for all.
    /// </summary>
    public int FailuresBeforeBlock { get; set; } = 0;

    /// <summary>Comma-separated user ids that are never blocked (the portal's service account).</summary>
    public string? ExemptUserIds { get; set; }

    private HashSet<Guid>? _exempt;
    internal HashSet<Guid> ExemptUserIdSet => _exempt ??= (ExemptUserIds ?? string.Empty)
        .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty)
        .Where(g => g != Guid.Empty)
        .ToHashSet();
    public int FailureWindowMinutes { get; set; } = 10;
    public int BlockMinutes { get; set; } = 15;

    /// <summary>Request body caps outside the document endpoints.</summary>
    public long FormBodyLimitBytes { get; set; } = 1024 * 1024;
    public long WebhookBodyLimitBytes { get; set; } = 256 * 1024;
}
