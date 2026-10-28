using System.Net;
using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Backoffice;

/// <summary>
/// Startup rules for the <c>P4Backoffice</c> section. Pure — no host needed to test it.
///
/// Every problem is returned at once so one restart surfaces all of them, and every
/// message names the environment variable to fix.
/// </summary>
public static class BackofficeOptionsValidator
{
    public const string SecretKeyPrefix = "p4sk_";
    public const string TestKeyPrefix = "p4sk_test_";
    public const string LiveKeyPrefix = "p4sk_live_";
    public const string WebhookSecretPrefix = "whsec_";

    /// <summary>
    /// Shortest accepted HMAC key behind <c>whsec_</c>, in bytes. The service generates
    /// 24 random bytes, so a shorter one is a truncated or placeholder value.
    /// </summary>
    public const int MinWebhookSecretBytes = 24;

    public const string LiveEnvironment = "live";
    public const string TestEnvironment = "test";

    /// <param name="options">The <c>P4Backoffice</c> section.</param>
    /// <param name="rejectInvalidModes">
    /// True (Production): a mode that is not Off or On stops the start. False: the module stays
    /// Off and the start report warns (see <see cref="InvalidModes"/>).
    /// </param>
    /// <param name="requireBilling">
    /// True (Production): payments go through the billing service only, so a hosted installation
    /// without <c>Modules:Billing=On</c> would start with no way to take a payment. A private
    /// server (no billing at all) is exempt.
    /// </param>
    public static IReadOnlyList<string> Validate(P4BackofficeProductOptions options, bool rejectInvalidModes = true, bool requireBilling = false)
    {
        var problems = new List<string>();

        if (rejectInvalidModes)
        {
            foreach (var (setting, value) in InvalidModes(options))
            {
                problems.Add($"{setting} is \"{value}\"; expected Off or On.");
            }
        }

        CheckWebhookSecret(problems, options.Webhook.Secret, "Webhook:Secret");
        CheckWebhookSecret(problems, options.Webhook.PreviousSecret, "Webhook:PreviousSecret");

        if (options.Email.FallbackAfter < TimeSpan.Zero)
        {
            problems.Add($"{Setting("Email:FallbackAfter")} must not be negative.");
        }
        if (options.DocumentsTtl is { } ttl && ttl <= TimeSpan.Zero)
        {
            problems.Add($"{Setting("DocumentsTtl")} must be positive.");
        }
        if (options.Outbox.GiveUpAfter is { } giveUp
            && (giveUp <= TimeSpan.Zero || giveUp >= DotNetSigningServer.Services.Backoffice.Outbox.RetryPolicy.IdempotencyWindow))
        {
            problems.Add($"{Setting("Outbox:GiveUpAfter")} must be positive and under 24 h (the service keeps an Idempotency-Key 24 h).");
        }
        if (options.Polling.Interval is { } interval && interval <= TimeSpan.Zero)
        {
            problems.Add($"{Setting("Polling:Interval")} must be positive.");
        }
        CheckConsentDocuments(problems, options.Consents, options.RequestedModeFor(BackofficeModule.Docs));
        CheckBilling(problems, options);
        if (requireBilling) RequireBilling(problems, options);

        // A self-hosted installation is forced Off whatever it asks for, so a key or URL
        // left over there is not a reason to refuse to start (it is logged instead). A build
        // without the SDK is validated as configured: the configuration is wrong either way.
        var connectionNeeded = options.DisabledReason != BackofficeDisabledReason.PrivateServer
                               && options.AnyRequested;
        if (connectionNeeded)
        {
            CheckBaseUrl(problems, options.BaseUrl);
            CheckSecretKey(problems, options.SecretKey);
            CheckKeyKind(problems, options.SecretKey, options.Environment);
        }

        return problems;
    }

    /// <summary>
    /// Payments through the service charge catalog prices only (lookup keys of the price list
    /// in force), which the product knows only with <c>Modules:Pricing=On</c>.
    /// </summary>
    private static void CheckBilling(List<string> problems, P4BackofficeProductOptions options)
    {
        if (options.DisabledReason != BackofficeDisabledReason.PrivateServer
            && options.RequestedModeFor(BackofficeModule.Billing) == BackofficeMode.On
            && options.RequestedModeFor(BackofficeModule.Pricing) != BackofficeMode.On)
        {
            problems.Add($"{Setting("Modules:Billing")} is On, which needs {Setting("Modules:Pricing")} On (payments use the price list's lookup keys).");
        }
    }

    private static void RequireBilling(List<string> problems, P4BackofficeProductOptions options)
    {
        if (options.DisabledReason != BackofficeDisabledReason.PrivateServer
            && options.RequestedModeFor(BackofficeModule.Billing) != BackofficeMode.On)
        {
            problems.Add($"{Setting("Modules:Billing")} must be On in production: payments go through the billing service, this product has no payment provider of its own.");
        }
    }

    /// <summary>Document keys as the service accepts them (<c>ConsentInput.document</c>).</summary>
    private static readonly System.Text.RegularExpressions.Regex DocumentKey = new("^[a-z][a-z0-9_]{1,39}$");

    private static void CheckConsentDocuments(
        List<string> problems, P4BackofficeProductOptions.ConsentsOptions consents, BackofficeMode docsMode)
    {
        var granted = consents.EffectiveDocuments;
        var acknowledged = consents.EffectiveAcknowledged;
        foreach (var key in granted.Concat(acknowledged))
        {
            if (!DocumentKey.IsMatch(key ?? ""))
            {
                problems.Add($"{Setting("Consents:Documents")} / {Setting("Consents:Acknowledged")}: '{key}' is not a document key (lower case letters, digits, '_').");
            }
        }
        // Without the service's documents (Docs not On) a form can name a version only for a
        // document that has a static page; any other would make sign-up and re-consent answer 503.
        if (docsMode != BackofficeMode.On)
        {
            foreach (var key in granted.Concat(acknowledged).Where(k => DocumentKey.IsMatch(k ?? "")).Distinct(StringComparer.Ordinal))
            {
                if (Legal.LegalSlugMap.SlugFor(key) is null)
                {
                    problems.Add($"{Setting("Consents:Documents")} / {Setting("Consents:Acknowledged")}: '{key}' has no page in this product, so it needs {Setting("Modules:Docs")} On.");
                }
            }
        }
        foreach (var key in granted.Intersect(acknowledged, StringComparer.Ordinal))
        {
            problems.Add($"{Setting("Consents:Documents")} and {Setting("Consents:Acknowledged")} both list '{key}'.");
        }
    }

    /// <summary>
    /// A key of the wrong kind writes to the wrong data set: a test key in production would
    /// feed the service's test data, a live key on test would write live data (p4-backoffice
    /// #212). The kind follows the explicit <c>Environment</c>, not ASPNETCORE_ENVIRONMENT: the
    /// test deployment runs as Production with a test key. Refuse to start when it is missing,
    /// unknown or does not match the key.
    /// </summary>
    private static void CheckKeyKind(List<string> problems, string? key, string? environment)
    {
        var setting = $"{Setting("Environment")} / P4_BACKOFFICE_ENV";
        var target = environment?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(target))
        {
            problems.Add($"{setting} is required when any backoffice module is On; set it to {LiveEnvironment} (production) or {TestEnvironment}.");
            return;
        }
        if (target != LiveEnvironment && target != TestEnvironment)
        {
            problems.Add($"{setting} is \"{environment}\"; expected {LiveEnvironment} or {TestEnvironment}.");
            return;
        }

        var trimmed = key?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        if (target == LiveEnvironment && trimmed.StartsWith(TestKeyPrefix, StringComparison.Ordinal))
        {
            problems.Add($"{Setting("SecretKey")} is a test key ({TestKeyPrefix}…) but P4_BACKOFFICE_ENV is {LiveEnvironment}; use the live key ({LiveKeyPrefix}…).");
        }
        else if (target == TestEnvironment && trimmed.StartsWith(LiveKeyPrefix, StringComparison.Ordinal))
        {
            problems.Add($"{Setting("SecretKey")} is a live key ({LiveKeyPrefix}…) but P4_BACKOFFICE_ENV is {TestEnvironment}; use the test key ({TestKeyPrefix}…).");
        }
    }

    /// <summary>
    /// What may be shown of a key: its kind prefix only (<c>p4sk_live_…</c>), never the secret part.
    /// Null when no key is set.
    /// </summary>
    public static string? KeyDisplay(string? secretKey)
    {
        if (string.IsNullOrWhiteSpace(secretKey)) return null;
        var key = secretKey.Trim();
        if (key.StartsWith(LiveKeyPrefix, StringComparison.Ordinal)) return LiveKeyPrefix + "…";
        if (key.StartsWith(TestKeyPrefix, StringComparison.Ordinal)) return TestKeyPrefix + "…";
        if (key.StartsWith(SecretKeyPrefix, StringComparison.Ordinal)) return SecretKeyPrefix + "…";
        return "…";
    }

    /// <summary>"P4Backoffice:X (environment variable P4Backoffice__X)".</summary>
    public static string Setting(string key)
    {
        var path = P4BackofficeProductOptions.SectionName + ":" + key;
        return $"{path} (environment variable {path.Replace(":", "__")})";
    }

    /// <summary>The global and per-module settings whose value is neither Off nor On.</summary>
    public static IReadOnlyList<(string Setting, string? Value)> InvalidModes(P4BackofficeProductOptions options)
    {
        var invalid = new List<(string, string?)>();
        if (!P4BackofficeProductOptions.TryParseMode(options.Mode, out _)) invalid.Add((Setting("Mode"), options.Mode));
        foreach (var module in Enum.GetValues<BackofficeModule>())
        {
            var value = options.Modules.Get(module);
            if (!P4BackofficeProductOptions.TryParseMode(value, out _)) invalid.Add((Setting($"Modules:{module}"), value));
        }
        return invalid;
    }

    private static void CheckWebhookSecret(List<string> problems, string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var trimmed = value.Trim();
        if (!trimmed.StartsWith(WebhookSecretPrefix, StringComparison.Ordinal))
        {
            problems.Add($"{Setting(key)} must be empty or start with {WebhookSecretPrefix}.");
        }
        else if (DecodedLength(trimmed[WebhookSecretPrefix.Length..]) is not { } length)
        {
            problems.Add($"{Setting(key)} must be {WebhookSecretPrefix} followed by base64 (copy it from the webhook endpoint).");
        }
        else if (length < MinWebhookSecretBytes)
        {
            problems.Add($"{Setting(key)} is too short: the key after {WebhookSecretPrefix} decodes to {length} bytes, "
                         + $"at least {MinWebhookSecretBytes} are required (copy the whole secret from the webhook endpoint).");
        }
    }

    /// <summary>Decoded byte length of a non-empty base64 value, or null when it is not base64.</summary>
    private static int? DecodedLength(string value)
    {
        if (value.Length == 0) return null;
        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out var written) ? written : null;
    }

    private static void CheckBaseUrl(List<string> problems, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{Setting("BaseUrl")} is required when any backoffice module is On.");
            return;
        }
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            problems.Add($"{Setting("BaseUrl")} must be an absolute https URL.");
            return;
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri))
        {
            problems.Add($"{Setting("BaseUrl")} must use https (plain http is allowed only for localhost).");
        }
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback
        || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    private static void CheckSecretKey(List<string> problems, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{Setting("SecretKey")} is required when any backoffice module is On.");
        }
        else if (!value.Trim().StartsWith(SecretKeyPrefix, StringComparison.Ordinal))
        {
            problems.Add($"{Setting("SecretKey")} must start with {SecretKeyPrefix}.");
        }
    }
}

/// <summary>Adapter that runs <see cref="BackofficeOptionsValidator"/> through the options pipeline (ValidateOnStart).</summary>
internal sealed class BackofficeOptionsValidation(IHostEnvironment environment) : IValidateOptions<P4BackofficeProductOptions>
{
    public ValidateOptionsResult Validate(string? name, P4BackofficeProductOptions options)
    {
        var problems = BackofficeOptionsValidator.Validate(options, rejectInvalidModes: environment.IsProduction(), requireBilling: environment.IsProduction());
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
