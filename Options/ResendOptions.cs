namespace DotNetSigningServer.Options;

/// <summary>
/// Transactional e-mail via Resend. Everything here comes from configuration
/// (<c>RESEND_API_KEY</c>, <c>EMAIL_FROM</c>, <c>EMAIL_REPLY_TO</c>) — no
/// hardcoded sender, so the deployment's own verified domain is the only thing
/// that can end up on outgoing mail.
/// </summary>
public class ResendOptions
{
    public string? ApiKey { get; set; }

    /// <summary>
    /// Envelope sender, e.g. <c>Performance4PDF &lt;noreply@send.example.com&gt;</c>.
    /// Its domain must be verified in Resend (DKIM published under
    /// <c>resend._domainkey.&lt;domain&gt;</c>) or Resend rejects the send.
    /// Required whenever <see cref="ApiKey"/> is set; Program.cs enforces that
    /// at startup rather than letting mail fail one message at a time.
    /// </summary>
    public string? From { get; set; }

    public string? ReplyTo { get; set; }

    /// <summary>True when e-mail sending is both enabled and usable.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(From);
}
