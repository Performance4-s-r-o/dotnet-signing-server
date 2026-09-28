using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice.Outbox;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Email;

/// <summary>
/// <c>IEmailSender</c> while <c>P4Backoffice:Modules:Email</c> is On: the HTML is still rendered
/// locally, and the message is queued as outbox item <c>email.raw</c> (encrypted), sent to the
/// service's <c>POST /v1/emails</c> by the dispatcher. Nothing is sent during the call, so a
/// service outage adds no latency to sign-in or sign-up. Critical messages that the service does
/// not deliver in time are sent directly through Resend by <c>BreakGlassEmailFallback</c>.
/// </summary>
public sealed partial class BackofficeOutboxEmailSender : ITransactionalEmailSender
{
    public const string OutboxKind = "email.raw";

    /// <summary>Allowed tag value characters of the service (<c>[A-Za-z0-9_-]{1,256}</c>).</summary>
    public const int MaxTagLength = 256;

    private readonly IBackofficeOutbox _outbox;
    private readonly ApplicationDbContext _db;
    private readonly ResendOptions _resend;

    public BackofficeOutboxEmailSender(IBackofficeOutbox outbox, ApplicationDbContext db, IOptions<ResendOptions> resend)
    {
        _outbox = outbox;
        _db = db;
        _resend = resend.Value;
    }

    public Task SendAsync(string toEmail, string subject, string htmlBody) =>
        SendAsync(toEmail, subject, htmlBody, null);

    /// <summary>Queues the message and saves (together with whatever else the caller has pending).</summary>
    public async Task SendAsync(string toEmail, string subject, string htmlBody, EmailSendOptions? options)
    {
        Enqueue(toEmail, subject, htmlBody, options);
        await _db.SaveChangesAsync();
    }

    public void Enqueue(string toEmail, string subject, string htmlBody, EmailSendOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toEmail);
        var payload = BuildPayload(toEmail, subject, htmlBody, options, _resend);
        _outbox.Enqueue(
            OutboxKind,
            payload,
            payload.Critical,
            options?.UserId is { } userId ? $"user:{userId}" : null);
    }

    public static EmailRawPayload BuildPayload(
        string toEmail, string subject, string htmlBody, EmailSendOptions? options, ResendOptions? resend = null) => new()
        {
            To = toEmail,
            Subject = subject,
            Html = htmlBody,
            // The service sends from the environment's default sender unless told otherwise;
            // EMAIL_FROM keeps the product's own verified domain on the message.
            From = string.IsNullOrWhiteSpace(resend?.From) ? null : resend!.From,
            ReplyTo = string.IsNullOrWhiteSpace(resend?.ReplyTo) ? null : resend!.ReplyTo,
            Tags = Tags(options),
            Critical = options?.IsCritical ?? false,
        };

    /// <summary><c>template</c>, <c>locale</c>, <c>user_id</c> — whichever are known, sanitised for the service.</summary>
    public static Dictionary<string, string> Tags(EmailSendOptions? options)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (options == null) return tags;
        Add(tags, "template", options.TemplateId);
        Add(tags, "locale", options.Locale);
        Add(tags, "user_id", options.UserId?.ToString("D"));
        return tags;
    }

    private static void Add(Dictionary<string, string> tags, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var clean = InvalidTagChars().Replace(value.Trim(), "");
        if (clean.Length > MaxTagLength) clean = clean[..MaxTagLength];
        if (clean.Length > 0) tags[key] = clean;
    }

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex InvalidTagChars();
}

/// <summary>Body of <c>POST /v1/emails</c> in raw mode (<c>EmailInput</c>), stored encrypted in the outbox.</summary>
public sealed class EmailRawPayload
{
    public string To { get; init; } = "";

    public string Subject { get; init; } = "";

    public string Html { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReplyTo { get; init; }

    public Dictionary<string, string> Tags { get; init; } = new();

    public bool Critical { get; init; }

    public string Category { get; init; } = "transactional";
}
