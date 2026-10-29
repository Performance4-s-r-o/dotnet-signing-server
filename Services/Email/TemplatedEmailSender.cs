using System.Text.Json.Serialization;
using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Outbox;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Email;

/// <summary>Which way a templated e-mail goes.</summary>
public enum TemplatedEmailRoute
{
    /// <summary>Rendered locally and sent through <see cref="IEmailSender"/>.</summary>
    Local,

    /// <summary>Outbox item <c>email.template</c>: the service renders its own template.</summary>
    ServiceTemplate,
}

/// <inheritdoc />
public sealed class TemplatedEmailSender : ITemplatedEmailSender
{
    public const string OutboxKind = "email.template";

    private readonly IEmailSender _emailSender;
    private readonly IEmailTemplateRenderer _renderer;
    private readonly IOptionsMonitor<P4BackofficeProductOptions> _options;
    private readonly IBackofficeOutbox _outbox;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<TemplatedEmailSender> _logger;

    public TemplatedEmailSender(
        IEmailSender emailSender,
        IEmailTemplateRenderer renderer,
        IOptionsMonitor<P4BackofficeProductOptions> options,
        IBackofficeOutbox outbox,
        ApplicationDbContext db,
        ILogger<TemplatedEmailSender> logger)
    {
        _emailSender = emailSender;
        _renderer = renderer;
        _options = options;
        _outbox = outbox;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// <see cref="TemplatedEmailRoute.ServiceTemplate"/> only while the Email module is On and
    /// the key is listed in <c>TemplateKeys</c> (read now, not at startup).
    /// </summary>
    public TemplatedEmailRoute RouteFor(string templateKey)
    {
        var options = _options.CurrentValue;
        // A gateway knows no service templates: everything is rendered locally.
        return options.ModeFor(BackofficeModule.Email) == BackofficeMode.On
               && !options.GatewayOnly
               && IsListed(options, templateKey)
               && _emailSender is ITransactionalEmailSender
            ? TemplatedEmailRoute.ServiceTemplate
            : TemplatedEmailRoute.Local;
    }

    public bool TryEnqueue(
        string templateKey, string toEmail, string locale,
        IReadOnlyDictionary<string, string?> variables, EmailSendOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(toEmail);
        var sendOptions = OptionsFor(templateKey, locale, options);

        if (RouteFor(templateKey) == TemplatedEmailRoute.ServiceTemplate)
        {
            EnqueueTemplate(templateKey, toEmail, locale, variables, sendOptions);
            return true;
        }

        if (_emailSender is not ITransactionalEmailSender)
        {
            // Sent during SendAsync, after the caller's save (as before).
            return false;
        }

        var rendered = _renderer.Render(templateKey, locale, variables);
        return _emailSender.TryEnqueue(toEmail, rendered.Subject, rendered.HtmlBody, sendOptions);
    }

    public async Task SendAsync(
        string templateKey, string toEmail, string locale,
        IReadOnlyDictionary<string, string?> variables, EmailSendOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(toEmail);
        var sendOptions = OptionsFor(templateKey, locale, options);

        if (RouteFor(templateKey) == TemplatedEmailRoute.ServiceTemplate)
        {
            EnqueueTemplate(templateKey, toEmail, locale, variables, sendOptions);
            await _db.SaveChangesAsync();
            return;
        }

        var rendered = _renderer.Render(templateKey, locale, variables);
        await _emailSender.SendAsync(toEmail, rendered.Subject, rendered.HtmlBody, sendOptions);
    }

    private void EnqueueTemplate(
        string templateKey, string toEmail, string locale,
        IReadOnlyDictionary<string, string?> variables, EmailSendOptions options)
    {
        var email = _options.CurrentValue.Email;
        var payload = BuildPayload(templateKey, toEmail, locale, variables, options, email.EffectiveFrom, email.EffectiveReplyTo);
        _outbox.Enqueue(
            OutboxKind,
            payload,
            options.IsCritical,
            options.UserId is { } userId ? $"user:{userId}" : null);
    }

    public static EmailTemplatePayload BuildPayload(
        string templateKey, string toEmail, string locale, IReadOnlyDictionary<string, string?> variables,
        EmailSendOptions? options, string? from = null, string? replyTo = null) => new()
        {
            To = toEmail,
            Template = templateKey,
            // Every variable of the service's templates is a required string.
            Variables = variables.ToDictionary(v => v.Key, v => v.Value ?? "", StringComparer.Ordinal),
            Locale = EmailTemplateLocales.ForService(locale),
            From = string.IsNullOrWhiteSpace(from) ? null : from,
            ReplyTo = string.IsNullOrWhiteSpace(replyTo) ? null : replyTo,
            Tags = BackofficeOutboxEmailSender.Tags(options),
        };

    private static EmailSendOptions OptionsFor(string templateKey, string locale, EmailSendOptions? options) =>
        options == null ? new EmailSendOptions(templateKey, locale) : options with { TemplateId = templateKey };

    private static bool IsListed(P4BackofficeProductOptions options, string templateKey) =>
        options.Email.Selects(templateKey);
}

/// <summary>Languages the service's templates are published in; the local templates have the same two.</summary>
public static class EmailTemplateLocales
{
    public const string Default = "en";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal) { "cs", "en" };

    /// <summary><c>cs-CZ</c> → <c>cs</c>; anything not published (de, es, …) → <c>en</c>, as the local renderer does.</summary>
    public static string ForService(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return Default;
        var language = locale.Trim().ToLowerInvariant();
        var dash = language.IndexOfAny(['-', '_']);
        if (dash > 0) language = language[..dash];
        return Supported.Contains(language) ? language : Default;
    }
}

/// <summary>
/// Body of <c>POST /v1/emails</c> in template mode, stored encrypted in the outbox.
/// Category and <c>critical</c> come from the service's template.
/// </summary>
public sealed class EmailTemplatePayload
{
    public string To { get; init; } = "";

    public string Template { get; init; } = "";

    public Dictionary<string, string> Variables { get; init; } = new();

    public string Locale { get; init; } = EmailTemplateLocales.Default;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReplyTo { get; init; }

    public Dictionary<string, string> Tags { get; init; } = new();
}
