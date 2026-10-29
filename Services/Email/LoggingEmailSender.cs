namespace DotNetSigningServer.Services.Email;

/// <summary>
/// Development stand-in for the backoffice outbox: nothing leaves the process. Used only
/// without a backoffice connection (Production refuses to start without one; a private
/// server is the one exception). The body is logged in Development only, because it holds
/// 2FA codes and reset links.
/// </summary>
public sealed class LoggingEmailSender(IHostEnvironment environment, ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string htmlBody)
    {
        if (environment.IsDevelopment())
        {
            logger.LogWarning("E-mail not sent (no backoffice connection). To: {To}, subject: {Subject}\n{Body}", toEmail, subject, htmlBody);
        }
        else
        {
            logger.LogWarning("E-mail not sent (no backoffice connection). To: {To}, subject: {Subject}", toEmail, subject);
        }
        return Task.CompletedTask;
    }
}
