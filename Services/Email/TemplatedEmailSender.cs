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
    private readonly ResendOptions _resend;
    private readonly ILogger<TemplatedEmailSender> _logger;
    private readonly TemplateShadowComparer? _shadow;

    public TemplatedEmailSender(
        IEmailSender emailSender,
        IEmailTemplateRenderer renderer,
        IOptionsMonitor<P4BackofficeProductOptions> options,
        IBackofficeOutbox outbox,
        ApplicationDbContext db,
        IOptions<ResendOptions> resend,
        ILogger<TemplatedEmailSender> logger,
        TemplateShadowComparer? shadow = null)
    {
        _emailSender = emailSender;
        _renderer = renderer;
        _options = options;
        _outbox = outbox;
        _db = db;
        _resend = resend.Value;
        _logger = logger;
        _shadow = shadow;
    }

    /// <summary>
    /// <see cref="TemplatedEmailRoute.ServiceTemplate"/> only while the Email module is On and
    /// the key is listed in <c>TemplateKeys</c> (read now, not at startup).
    /// </summary>
    public TemplatedEmailRoute RouteFor(string templateKey)
    {
        var options = _options.CurrentValue;
        return options.ModeFor(BackofficeModule.Email) == BackofficeMode.On
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
        StartShadowComparison(templateKey, locale, variables, rendered);
        await _emailSender.SendAsync(toEmail, rendered.Subject, rendered.HtmlBody, sendOptions);
    }

    /// <summary>Email=Shadow and the key listed: compare with the service's rendering in the background.</summary>
    private void StartShadowComparison(
        string templateKey, string locale, IReadOnlyDictionary<string, string?> variables, EmailTemplateResult rendered)
    {
        if (_shadow == null) return;
        var options = _options.CurrentValue;
        if (options.ModeFor(BackofficeModule.Email) != BackofficeMode.Shadow || !IsListed(options, templateKey)) return;
        _shadow.Schedule(templateKey, EmailTemplateLocales.ForService(locale), variables, rendered);
    }

    private void EnqueueTemplate(
        string templateKey, string toEmail, string locale,
        IReadOnlyDictionary<string, string?> variables, EmailSendOptions options)
    {
        EmailFallbackContent? fallback = null;
        if (options.IsCritical)
        {
            // Break-glass must not need the service: keep the local rendering in the
            // (encrypted) payload. It is never sent to the service.
            try
            {
                var rendered = _renderer.Render(templateKey, locale, variables);
                fallback = new EmailFallbackContent { Subject = rendered.Subject, Html = rendered.HtmlBody };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "E-mail template {TemplateKey} could not be rendered locally; queued without a break-glass copy",
                    templateKey);
            }
        }

        var payload = BuildPayload(templateKey, toEmail, locale, variables, options, fallback, _resend);
        _outbox.Enqueue(
            OutboxKind,
            payload,
            options.IsCritical,
            options.UserId is { } userId ? $"user:{userId}" : null);
    }

    public static EmailTemplatePayload BuildPayload(
        string templateKey, string toEmail, string locale, IReadOnlyDictionary<string, string?> variables,
        EmailSendOptions? options, EmailFallbackContent? fallback = null, ResendOptions? resend = null) => new()
        {
            To = toEmail,
            Template = templateKey,
            // Every variable of the service's templates is a required string.
            Variables = variables.ToDictionary(v => v.Key, v => v.Value ?? "", StringComparer.Ordinal),
            Locale = EmailTemplateLocales.ForService(locale),
            From = string.IsNullOrWhiteSpace(resend?.From) ? null : resend!.From,
            ReplyTo = string.IsNullOrWhiteSpace(resend?.ReplyTo) ? null : resend!.ReplyTo,
            Tags = BackofficeOutboxEmailSender.Tags(options),
            Fallback = fallback,
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
/// <see cref="Fallback"/> stays local: the handler removes it before sending.
/// Category and <c>critical</c> come from the service's template.
/// </summary>
public sealed class EmailTemplatePayload
{
    public const string FallbackProperty = "fallback";

    public string To { get; init; } = "";

    public string Template { get; init; } = "";

    public Dictionary<string, string> Variables { get; init; } = new();

    public string Locale { get; init; } = EmailTemplateLocales.Default;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReplyTo { get; init; }

    public Dictionary<string, string> Tags { get; init; } = new();

    /// <summary>Local rendering of a critical message, for break-glass only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmailFallbackContent? Fallback { get; init; }
}

/// <summary>Locally rendered subject and HTML kept with a critical <c>email.template</c> item.</summary>
public sealed class EmailFallbackContent
{
    public string Subject { get; init; } = "";

    public string Html { get; init; } = "";
}
