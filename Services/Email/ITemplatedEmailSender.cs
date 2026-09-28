namespace DotNetSigningServer.Services.Email;

/// <summary>
/// The one place that decides how a templated e-mail goes out:
/// <list type="bullet">
/// <item><c>Modules:Email=On</c> and the key listed in <c>P4Backoffice:Email:TemplateKeys</c> —
/// outbox item <c>email.template</c> (the service renders its own template; critical ones also
/// carry the local rendering for break-glass);</item>
/// <item>otherwise — rendered locally (<see cref="IEmailTemplateRenderer"/>) and handed to
/// <see cref="IEmailSender"/> (<c>email.raw</c> in the outbox when On, Resend otherwise).</item>
/// </list>
/// The key list is read on every call, so removing a key switches back to the local rendering
/// without a deployment.
/// </summary>
public interface ITemplatedEmailSender
{
    /// <summary>
    /// Adds the message to the caller's unit of work (the scoped <c>ApplicationDbContext</c>)
    /// without saving and returns true, when the chosen path queues into the outbox. Returns
    /// false when the message has to be sent during a call: the caller saves its own changes
    /// first and then calls <see cref="SendAsync"/>.
    /// </summary>
    bool TryEnqueue(
        string templateKey, string toEmail, string locale,
        IReadOnlyDictionary<string, string?> variables, EmailSendOptions? options = null);

    /// <summary>
    /// Sends (or queues and saves, together with whatever else the caller has pending). Throws
    /// when a direct send fails, like <see cref="IEmailSender.SendAsync(string, string, string)"/>.
    /// </summary>
    Task SendAsync(
        string templateKey, string toEmail, string locale,
        IReadOnlyDictionary<string, string?> variables, EmailSendOptions? options = null);
}
