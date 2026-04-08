namespace DotNetSigningServer.Services;

public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody);

    /// <summary>
    /// Sends an email with unsubscribe support. Non-critical emails are skipped when
    /// the user has disabled notifications. Critical emails (e.g. 2FA codes) are always sent.
    /// </summary>
    Task SendAsync(string toEmail, string subject, string htmlBody, string? settingsUrl, bool isCritical);
}
