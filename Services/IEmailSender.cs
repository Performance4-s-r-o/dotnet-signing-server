using DotNetSigningServer.Services.Email;

namespace DotNetSigningServer.Services;

public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody);

    /// <summary>
    /// Sends a message with its metadata (template, language, user, critical). Senders that do
    /// not use the metadata (Resend) send it like the three-argument overload.
    /// </summary>
    Task SendAsync(string toEmail, string subject, string htmlBody, EmailSendOptions? options) =>
        SendAsync(toEmail, subject, htmlBody);
}

/// <summary>
/// A sender that queues messages into the caller's unit of work (the backoffice outbox)
/// instead of sending them during the call.
/// </summary>
public interface ITransactionalEmailSender : IEmailSender
{
    /// <summary>
    /// Adds the message to the scoped <c>ApplicationDbContext</c> without saving: it is written
    /// by the caller's next <c>SaveChangesAsync</c>, together with the change it belongs to.
    /// </summary>
    void Enqueue(string toEmail, string subject, string htmlBody, EmailSendOptions? options);
}

public static class EmailSenderExtensions
{
    /// <summary>
    /// Queues the message into the caller's unit of work when the sender supports it and
    /// returns true; the caller's <c>SaveChangesAsync</c> then commits it. Returns false for a
    /// sender that sends immediately: the caller sends after its own save, as before.
    /// </summary>
    public static bool TryEnqueue(
        this IEmailSender sender, string toEmail, string subject, string htmlBody, EmailSendOptions? options)
    {
        if (sender is not ITransactionalEmailSender transactional) return false;
        transactional.Enqueue(toEmail, subject, htmlBody, options);
        return true;
    }
}
