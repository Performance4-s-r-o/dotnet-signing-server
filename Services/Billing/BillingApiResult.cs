using System.Text.Json;

namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// One answer of the billing API: the status and body, or no answer at all (network error,
/// timeout). Errors are RFC 9457 <c>problem+json</c> with a machine <c>code</c> and, for
/// Stripe's refusals, <c>stripe_code</c>; a validation error adds
/// <c>errors: [{path, message}]</c>, read into <see cref="ProblemErrors"/>.
/// </summary>
public sealed record BillingApiResult(
    int? Status,
    string? Body,
    string? ProblemCode,
    string? StripeCode,
    string? ProblemErrors = null)
{
    /// <summary>At most this many <c>errors</c> entries go into the log line.</summary>
    internal const int MaxProblemErrors = 5;

    /// <summary>Each <c>path</c> and <c>message</c> is cut to this many characters.</summary>
    internal const int MaxProblemErrorLength = 100;

    public bool IsSuccess => Status is >= 200 and < 300;

    public static BillingApiResult NoAnswer() => new(null, null, null, null);

    /// <summary>
    /// Reads <c>code</c>, <c>stripe_code</c> and the <c>errors</c> summary of an error body; a body
    /// that is not JSON leaves them null.
    /// </summary>
    public static BillingApiResult From(int status, string? body)
    {
        if (status < 400 || string.IsNullOrWhiteSpace(body)) return new(status, body, null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new(status, body, null, null);
            return new(
                status,
                body,
                BillingJson.String(doc.RootElement, "code"),
                BillingJson.String(doc.RootElement, "stripe_code"),
                SummarizeErrors(doc.RootElement));
        }
        catch (JsonException)
        {
            return new(status, body, null, null);
        }
    }

    /// <summary>
    /// For logs: <c>HTTP 422 validation_failed [success_url: return_url_origin_not_allowed]</c>,
    /// <c>HTTP 422 stripe_error (card_declined)</c>, <c>no answer</c>. Never the body.
    /// </summary>
    public string Describe()
    {
        if (Status is null) return "no answer";
        var text = ProblemCode is null ? $"HTTP {Status}" : $"HTTP {Status} {ProblemCode}";
        if (ProblemErrors is not null) text = $"{text} [{ProblemErrors}]";
        return StripeCode is null ? text : $"{text} ({StripeCode})";
    }

    /// <summary>
    /// <c>errors: [{path, message}]</c> as <c>path: message; path: message</c>. Only those two
    /// fields — both are short codes from the service (<c>success_url</c>,
    /// <c>return_url_not_https</c>); any other field (a rejected value could be an e-mail or a
    /// name) is never read, and a path or message that is not a code (letters, digits,
    /// <c>_ . - [ ] /</c>) is replaced by <see cref="NotACode"/>. Capped at
    /// <see cref="MaxProblemErrors"/> entries, each field at <see cref="MaxProblemErrorLength"/>
    /// characters. Null when there is nothing to show.
    /// </summary>
    internal static string? SummarizeErrors(JsonElement problem)
    {
        if (!problem.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) return null;
        var parts = new List<string>();
        var total = 0;
        foreach (var error in errors.EnumerateArray())
        {
            var path = Clean(BillingJson.String(error, "path"));
            var message = Clean(BillingJson.String(error, "message"));
            if (path is null && message is null) continue;
            total++;
            if (parts.Count >= MaxProblemErrors) continue;
            parts.Add(path is null ? message! : message is null ? path : $"{path}: {message}");
        }
        if (parts.Count == 0) return null;
        var text = string.Join("; ", parts);
        return total > parts.Count ? $"{text}; +{total - parts.Count} more" : text;
    }

    /// <summary>Stands in for a field that is not a code (free text could carry a rejected value).</summary>
    internal const string NotACode = "(not a code)";

    private static string? Clean(string? value)
    {
        if (value is null) return null;
        var text = value.Trim();
        if (text.Length == 0) return null;
        // Codes only: an allowlist, not a blocklist — free text (an e-mail echoed in a message),
        // control, separator or bidi characters never reach a log line.
        if (!text.All(IsCodeChar)) return NotACode;
        return text.Length > MaxProblemErrorLength ? text[..MaxProblemErrorLength] + "…" : text;
    }

    private static bool IsCodeChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '.' or '-' or '[' or ']' or '/';

    /// <summary>The success body parsed; throws <see cref="BillingApiException"/> for anything else.</summary>
    public JsonDocument RequireJson(string operation)
    {
        if (!IsSuccess || string.IsNullOrWhiteSpace(Body)) throw new BillingApiException(operation, this);
        try
        {
            return JsonDocument.Parse(Body);
        }
        catch (JsonException)
        {
            throw new BillingApiException(operation, this);
        }
    }
}

/// <summary>
/// The billing API refused or did not answer. The message names the operation, the problem
/// code and the <c>errors</c> codes (path + message) only — never personal data or the body.
/// </summary>
public sealed class BillingApiException : Exception
{
    public BillingApiException(string operation, BillingApiResult result)
        : base($"Billing API {operation}: {result.Describe()}")
    {
        Result = result;
    }

    public BillingApiResult Result { get; }

    /// <summary>The service or Stripe is down (5xx or no answer) rather than the request being refused.</summary>
    public bool IsOutage => Result.Status is null || Result.Status >= 500;
}

/// <summary>Small readers for the API's JSON. Pure.</summary>
internal static class BillingJson
{
    public static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static long? Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    public static DateTime? Date(JsonElement element, string name) =>
        String(element, name) is { } text
        && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
            ? date.UtcDateTime
            : null;

    public static IReadOnlyDictionary<string, string> StringMap(JsonElement element, string name)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return map;
        }
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String) map[property.Name] = property.Value.GetString()!;
        }
        return map;
    }
}
