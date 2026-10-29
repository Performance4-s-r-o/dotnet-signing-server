using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Email;

/// <summary>
/// Development stand-in for the backoffice outbox: nothing leaves the process. Outside
/// Development it throws, so a flow that waits for an e-mail (2FA code, reset) shows its
/// failure instead of "code sent". Production never gets here (it refuses to start without a
/// connection); a private server without a gateway does.
/// The recipient is logged as a redacted tag (<see cref="LogRedaction.Email"/>), the body only with <c>Email:LogBodies=true</c> in Development.
/// </summary>
public sealed class EmailNotConfiguredException()
    : InvalidOperationException("E-mail cannot be sent: P4Backoffice BaseUrl and SecretKey are not configured.");

public sealed class LoggingEmailSender(
    IHostEnvironment environment,
    IOptions<P4BackofficeProductOptions> options,
    ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string htmlBody)
    {
        if (!environment.IsDevelopment())
        {
            throw new EmailNotConfiguredException();
        }

        logger.LogWarning("E-mail not sent (no backoffice connection). To: {To}, subject: {Subject}", LogRedaction.Email(toEmail), subject);
        if (options.Value.Email.LogBodies)
        {
            logger.LogDebug("E-mail body for {To}:\n{Body}", LogRedaction.Email(toEmail), htmlBody);
        }
        return Task.CompletedTask;
    }
}
