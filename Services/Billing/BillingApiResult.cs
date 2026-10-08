using System.Text.Json;

namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// One answer of the billing API: the status and body, or no answer at all (network error,
/// timeout). Errors are RFC 9457 <c>problem+json</c> with a machine <c>code</c> and, for
/// Stripe's refusals, <c>stripe_code</c>.
/// </summary>
public sealed record BillingApiResult(int? Status, string? Body, string? ProblemCode, string? StripeCode)
{
    public bool IsSuccess => Status is >= 200 and < 300;

    public static BillingApiResult NoAnswer() => new(null, null, null, null);

    /// <summary>Reads <c>code</c> and <c>stripe_code</c> of an error body; a body that is not JSON leaves them null.</summary>
    public static BillingApiResult From(int status, string? body)
    {
        if (status < 400 || string.IsNullOrWhiteSpace(body)) return new(status, body, null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new(status, body, null, null);
            return new(status, body, BillingJson.String(doc.RootElement, "code"), BillingJson.String(doc.RootElement, "stripe_code"));
        }
        catch (JsonException)
        {
            return new(status, body, null, null);
        }
    }

    /// <summary>For logs: <c>HTTP 422 stripe_error (card_declined)</c>, <c>no answer</c>. Never the body.</summary>
    public string Describe()
    {
        if (Status is null) return "no answer";
        var text = ProblemCode is null ? $"HTTP {Status}" : $"HTTP {Status} {ProblemCode}";
        return StripeCode is null ? text : $"{text} ({StripeCode})";
    }

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
/// The billing API refused or did not answer. The message names the operation and the
/// problem code only — never personal data or the body.
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
