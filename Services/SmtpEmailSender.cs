using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Mail;

namespace DotNetSigningServer.Services;

public class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task SendAsync(string toEmail, string subject, string htmlBody)
        => SendAsync(toEmail, subject, htmlBody, settingsUrl: null, isCritical: true);

    public async Task SendAsync(string toEmail, string subject, string htmlBody, string? settingsUrl, bool isCritical)
    {
        // If SMTP is not configured, log and exit gracefully to avoid failing sign-up in dev.
        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            _logger.LogWarning("SMTP host not configured. Skipping email send (subject: {Subject}).", subject);
            return;
        }

        // Append unsubscribe footer when a settings URL is provided
        if (!string.IsNullOrWhiteSpace(settingsUrl))
        {
            htmlBody += $@"<hr style=""margin-top:32px;border:none;border-top:1px solid #e0e0e0""/>
<p style=""font-size:12px;color:#888"">You received this email because you have an account at P4PDF.
<a href=""{settingsUrl}"">Manage notification preferences</a>.</p>";
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };
        message.To.Add(new MailAddress(toEmail));

        // RFC 2369 List-Unsubscribe header (GDPR / CAN-SPAM)
        if (!string.IsNullOrWhiteSpace(settingsUrl))
        {
            message.Headers.Add("List-Unsubscribe", $"<{settingsUrl}>");
            message.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click-Unsubscribe");
        }

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseSsl
        };

        if (!string.IsNullOrWhiteSpace(_options.Username) && !string.IsNullOrWhiteSpace(_options.Password))
        {
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);
        }

        try
        {
            await client.SendMailAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email (subject: {Subject})", subject);
            throw;
        }
    }
}
