using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Email;

/// <summary>
/// Development stand-in for the backoffice outbox: nothing leaves the process. Outside
/// Development it throws, so a flow that waits for an e-mail (2FA code, reset) shows its
/// failure instead of "code sent". Production never gets here (it refuses to start without a
/// connection); a private server without a gateway does.
/// The recipient is logged masked, the body only with <c>Email:LogBodies=true</c> in Development.
/// </summary>
public sealed class LoggingEmailSender(
    IHostEnvironment environment,
    IOptions<P4BackofficeProductOptions> options,
    ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string htmlBody)
    {
        if (!environment.IsDevelopment())
        {
            logger.LogError("E-mail not sent: no backoffice connection (subject: {Subject})", subject);
            throw new InvalidOperationException(
                "E-mail cannot be sent: P4Backoffice BaseUrl and SecretKey are not configured.");
        }

        logger.LogWarning("E-mail not sent (no backoffice connection). To: {To}, subject: {Subject}", Mask(toEmail), subject);
        if (options.Value.Email.LogBodies)
        {
            logger.LogDebug("E-mail body for {To}:\n{Body}", Mask(toEmail), htmlBody);
        }
        return Task.CompletedTask;
    }

    /// <summary><c>jan@example.com</c> becomes <c>j***@example.com</c>.</summary>
    public static string Mask(string? address)
    {
        if (string.IsNullOrEmpty(address)) return "";
        var at = address.IndexOf('@');
        return at <= 0 ? "***" : address[0] + "***" + address[at..];
    }
}
