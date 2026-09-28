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

    public static IReadOnlyList<string> Validate(P4BackofficeProductOptions options)
    {
        var problems = new List<string>();

        CheckMode(problems, options.Mode, "Mode");
        foreach (var module in Enum.GetValues<BackofficeModule>())
        {
            CheckMode(problems, options.Modules.Get(module), $"Modules:{module}");
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
        if (options.Polling.Interval is { } interval && interval <= TimeSpan.Zero)
        {
            problems.Add($"{Setting("Polling:Interval")} must be positive.");
        }
        CheckConsentDocuments(problems, options.Consents);

        // A self-hosted installation is forced Off whatever it asks for, so a key or URL
        // left over there is not a reason to refuse to start (it is logged instead). A build
        // without the SDK is validated as configured: the configuration is wrong either way.
        var connectionNeeded = options.DisabledReason != BackofficeDisabledReason.PrivateServer
                               && options.AnyRequested;
        if (connectionNeeded)
        {
            CheckBaseUrl(problems, options.BaseUrl);
            CheckSecretKey(problems, options.SecretKey);
        }

        return problems;
    }

    /// <summary>Document keys as the service accepts them (<c>ConsentInput.document</c>).</summary>
    private static readonly System.Text.RegularExpressions.Regex DocumentKey = new("^[a-z][a-z0-9_]{1,39}$");

    private static void CheckConsentDocuments(List<string> problems, P4BackofficeProductOptions.ConsentsOptions consents)
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
        foreach (var key in granted.Intersect(acknowledged, StringComparer.Ordinal))
        {
            problems.Add($"{Setting("Consents:Documents")} and {Setting("Consents:Acknowledged")} both list '{key}'.");
        }
    }

    /// <summary>A test key in production is allowed (staging-like setups) but worth a warning.</summary>
    public static bool IsTestKeyInProduction(P4BackofficeProductOptions options, bool isProduction) =>
        isProduction
        && options.AnyEnabled
        && options.SecretKey?.Trim().StartsWith(TestKeyPrefix, StringComparison.Ordinal) == true;

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

    private static void CheckMode(List<string> problems, string? value, string key)
    {
        if (!P4BackofficeProductOptions.TryParseMode(value, out _))
        {
            problems.Add($"{Setting(key)} is \"{value}\"; expected Off, Shadow or On.");
        }
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
            problems.Add($"{Setting("BaseUrl")} is required when any backoffice module is Shadow or On.");
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
            problems.Add($"{Setting("SecretKey")} is required when any backoffice module is Shadow or On.");
        }
        else if (!value.Trim().StartsWith(SecretKeyPrefix, StringComparison.Ordinal))
        {
            problems.Add($"{Setting("SecretKey")} must start with {SecretKeyPrefix}.");
        }
    }
}

/// <summary>Adapter that runs <see cref="BackofficeOptionsValidator"/> through the options pipeline (ValidateOnStart).</summary>
internal sealed class BackofficeOptionsValidation : IValidateOptions<P4BackofficeProductOptions>
{
    public ValidateOptionsResult Validate(string? name, P4BackofficeProductOptions options)
    {
        var problems = BackofficeOptionsValidator.Validate(options);
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
