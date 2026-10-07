using DotNetSigningServer.Services.Backoffice;

namespace DotNetSigningServer.Options;

/// <summary>
/// Product-side settings for the P4 Backoffice integration, section <c>P4Backoffice</c>.
///
/// Modes are kept as strings on purpose: an unknown value has to fail startup with a
/// message naming the setting, not with a binder exception. Parsing is case-insensitive.
/// </summary>
public class P4BackofficeProductOptions
{
    public const string SectionName = "P4Backoffice";

    /// <summary>Global mode (<c>Off</c>, <c>Shadow</c>, <c>On</c>); empty means <c>Off</c>.</summary>
    public string? Mode { get; set; }

    /// <summary>Per-module overrides; an empty value inherits <see cref="Mode"/>.</summary>
    public ModulesOptions Modules { get; set; } = new();

    /// <summary>Service root, e.g. <c>https://backoffice.example.com</c> (without <c>/v1</c>).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Secret key of the product environment (<c>p4sk_test_…</c> / <c>p4sk_live_…</c>).</summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// How long a legal document from the service is shown without revalidation (the SDK's
    /// setting of the same name). Empty = 5 minutes.
    /// </summary>
    public TimeSpan? DocumentsTtl { get; set; }

    /// <summary>
    /// How long the wording of a consent is shown without revalidation. Empty = 5 minutes,
    /// like <see cref="DocumentsTtl"/>; a published change is picked up by the next
    /// revalidation either way.
    /// </summary>
    public TimeSpan? ConsentPromptsTtl { get; set; }

    public EmailOptions Email { get; set; } = new();

    public WebhookOptions Webhook { get; set; } = new();

    public PollingOptions Polling { get; set; } = new();

    public ConsentsOptions Consents { get; set; } = new();

    public CookieWidgetOptions CookieWidget { get; set; } = new();

    /// <summary>
    /// Set by the integration after binding. When not <see cref="BackofficeDisabledReason.None"/>
    /// every module is Off whatever the configuration says.
    /// </summary>
    public BackofficeDisabledReason DisabledReason { get; internal set; }

    /// <summary>Effective mode of a module: the override, else the global mode, else Off.</summary>
    public BackofficeMode ModeFor(BackofficeModule module) =>
        DisabledReason != BackofficeDisabledReason.None ? BackofficeMode.Off : RequestedModeFor(module);

    /// <summary>
    /// Mode the configuration asks for, ignoring <see cref="DisabledReason"/>. An unparsable
    /// value counts as Off here; the validator refuses to start with it.
    /// </summary>
    public BackofficeMode RequestedModeFor(BackofficeModule module)
    {
        var raw = Modules.Get(module);
        if (string.IsNullOrWhiteSpace(raw)) raw = Mode;
        return TryParseMode(raw, out var mode) ? mode : BackofficeMode.Off;
    }

    /// <summary>True when at least one module is effectively Shadow or On.</summary>
    public bool AnyEnabled => Enum.GetValues<BackofficeModule>().Any(m => ModeFor(m) != BackofficeMode.Off);

    /// <summary>True when the configuration asks for at least one module to be Shadow or On.</summary>
    public bool AnyRequested => Enum.GetValues<BackofficeModule>().Any(m => RequestedModeFor(m) != BackofficeMode.Off);

    /// <summary>Empty/whitespace parses as Off; otherwise one of the enum names, any case.</summary>
    public static bool TryParseMode(string? value, out BackofficeMode mode)
    {
        mode = BackofficeMode.Off;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var trimmed = value.Trim();
        // Enum.TryParse would also accept numbers ("2"); only the names are valid here.
        foreach (var candidate in Enum.GetValues<BackofficeMode>())
        {
            if (string.Equals(candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                mode = candidate;
                return true;
            }
        }
        return false;
    }

    public class ModulesOptions
    {
        public string? Docs { get; set; }
        public string? Consents { get; set; }
        public string? Email { get; set; }
        public string? Pricing { get; set; }
        public string? Support { get; set; }

        public string? Get(BackofficeModule module) => module switch
        {
            BackofficeModule.Docs => Docs,
            BackofficeModule.Consents => Consents,
            BackofficeModule.Email => Email,
            BackofficeModule.Pricing => Pricing,
            BackofficeModule.Support => Support,
            _ => throw new ArgumentOutOfRangeException(nameof(module), module, null),
        };
    }

    public class EmailOptions
    {
        /// <summary><see cref="Templates"/> entry that selects every template.</summary>
        public const string Wildcard = "*";

        private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

        /// <summary>
        /// Template keys sent as service templates; every other message is sent as raw HTML.
        /// Empty by default — keys are added one at a time once the Email module is On.
        /// </summary>
        public List<string> TemplateKeys { get; set; } = new();

        /// <summary>
        /// The same selection in one value, for environments that pass a single string
        /// (<c>P4_BACKOFFICE_TEMPLATES</c>): <c>*</c> for every template, otherwise keys
        /// separated by commas or whitespace. Added to <see cref="TemplateKeys"/>, never
        /// replacing it.
        /// </summary>
        public string? Templates { get; set; }

        /// <summary>
        /// Whether the service renders this template. Read on every send, so narrowing the
        /// selection — or dropping back to local rendering — takes effect without a deploy.
        /// </summary>
        public bool Selects(string templateKey)
        {
            foreach (var entry in SelectedKeys())
            {
                if (entry == Wildcard || string.Equals(entry, templateKey, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Entries of both settings, trimmed, without the empty ones.</summary>
        public IEnumerable<string> SelectedKeys() =>
            TemplateKeys
                .Concat(Templates?.Split(Separators, StringSplitOptions.RemoveEmptyEntries) ?? [])
                .Select(k => k?.Trim() ?? string.Empty)
                .Where(k => k.Length > 0);

        /// <summary>Send critical messages (2FA, password reset, e-mail verification) directly when the service does not deliver them in time.</summary>
        public bool FallbackDirect { get; set; } = true;

        /// <summary>How long a critical message may wait in the outbox before the direct fallback.</summary>
        public TimeSpan FallbackAfter { get; set; } = TimeSpan.FromMinutes(1);
    }

    public class WebhookOptions
    {
        /// <summary>Signing secret of the webhook endpoint (<c>whsec_…</c>); empty disables the endpoint.</summary>
        public string? Secret { get; set; }

        /// <summary>Previous secret, accepted during rotation.</summary>
        public string? PreviousSecret { get; set; }

        /// <summary>True when the webhook endpoint accepts deliveries (a secret is set).</summary>
        public bool Configured => !string.IsNullOrWhiteSpace(Secret);
    }

    public class PollingOptions
    {
        /// <summary>Polling interval while webhooks are configured (they are the main channel).</summary>
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

        /// <summary>Polling interval without a webhook secret (polling is then the only channel).</summary>
        public static readonly TimeSpan DefaultIntervalWithoutWebhooks = TimeSpan.FromMinutes(2);

        /// <summary>
        /// How often <c>GET /v1/events</c> is polled. Empty = <see cref="DefaultInterval"/>, or
        /// <see cref="DefaultIntervalWithoutWebhooks"/> when no webhook secret is set.
        /// </summary>
        public TimeSpan? Interval { get; set; }

        /// <summary>The interval actually used.</summary>
        public TimeSpan EffectiveInterval(bool webhooksConfigured) =>
            Interval ?? (webhooksConfigured ? DefaultInterval : DefaultIntervalWithoutWebhooks);
    }

    public class ConsentsOptions
    {
        /// <summary>Documents a user grants consent to at sign-up (one checkbox).</summary>
        public static readonly IReadOnlyList<string> DefaultDocuments = new[] { "terms", "dpa" };

        /// <summary>Documents a user is only informed about at sign-up (acknowledged, not granted).</summary>
        public static readonly IReadOnlyList<string> DefaultAcknowledged = new[] { "privacy" };

        /// <summary>
        /// Overrides <see cref="DefaultDocuments"/>. Null rather than pre-filled: the configuration
        /// binder appends to an initialised collection instead of replacing it.
        /// </summary>
        public string[]? Documents { get; set; }

        /// <summary>Overrides <see cref="DefaultAcknowledged"/>.</summary>
        public string[]? Acknowledged { get; set; }

        public IReadOnlyList<string> EffectiveDocuments =>
            Documents is { Length: > 0 } ? Documents : DefaultDocuments;

        public IReadOnlyList<string> EffectiveAcknowledged =>
            Acknowledged is { Length: > 0 } ? Acknowledged : DefaultAcknowledged;
    }

    /// <summary>
    /// The service's cookie consent widget (banner). Loaded only with a publishable key — this
    /// product sets only strictly necessary cookies today, so it stays empty until an optional
    /// category (analytics, marketing, preferences) is added to the declaration in the service.
    /// Independent of the module modes: the widget talks to the service from the browser.
    /// </summary>
    public class CookieWidgetOptions
    {
        public const string DefaultUrl = "https://legal.performance4.cz";

        /// <summary>
        /// Origin browsers load <c>/v1/widget.js</c> from: the public legal viewer, which also
        /// passes the widget's API calls to the service (the service itself may stay private).
        /// </summary>
        public string? Url { get; set; }

        /// <summary>Publishable key of the product environment (<c>p4pk_live_…</c>); public by design.</summary>
        public string? PublishableKey { get; set; }

        /// <summary>The <c>&lt;script src&gt;</c> of the widget, or null when no key is set.</summary>
        public string? ScriptSrc =>
            string.IsNullOrWhiteSpace(PublishableKey)
                ? null
                : $"{(string.IsNullOrWhiteSpace(Url) ? DefaultUrl : Url.Trim()).TrimEnd('/')}/v1/widget.js";
    }
}
