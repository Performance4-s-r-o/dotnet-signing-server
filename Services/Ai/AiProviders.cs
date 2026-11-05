using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DotNetSigningServer.Services.Ai;

/// <summary>
/// A provider-neutral response schema; each client writes it in its provider's dialect
/// (JSON Schema for Anthropic structured outputs, OpenAPI-style <c>responseSchema</c> for Gemini).
/// </summary>
/// <param name="Type"><c>string</c>, <c>number</c>, <c>integer</c>, <c>boolean</c>, <c>object</c> or <c>array</c>.</param>
public sealed record AiSchema(
    string Type,
    bool Nullable = false,
    IReadOnlyList<string>? Enum = null,
    int? MaxLength = null,
    string? Format = null,
    IReadOnlyList<KeyValuePair<string, AiSchema>>? Properties = null,
    AiSchema? Items = null)
{
    public static AiSchema Object(params (string Name, AiSchema Schema)[] properties) =>
        new("object", Properties: properties.Select(p => KeyValuePair.Create(p.Name, p.Schema)).ToList());

    /// <summary>JSON Schema for Anthropic: every object closed and fully required, null through <c>anyOf</c>.
    /// String length is not supported there and is checked by the server after the answer.</summary>
    public JsonObject ToJsonSchema()
    {
        var node = new JsonObject { ["type"] = Type };
        if (Enum is { Count: > 0 }) node["enum"] = new JsonArray(Enum.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
        if (Format != null) node["format"] = Format;
        if (Properties != null)
        {
            var props = new JsonObject();
            foreach (var (name, schema) in Properties) props[name] = schema.ToJsonSchema();
            node["properties"] = props;
            node["required"] = new JsonArray(Properties.Select(p => (JsonNode)JsonValue.Create(p.Key)!).ToArray());
            node["additionalProperties"] = false;
        }
        if (Items != null) node["items"] = Items.ToJsonSchema();
        return Nullable ? new JsonObject { ["anyOf"] = new JsonArray(node, new JsonObject { ["type"] = "null" }) } : node;
    }

    /// <summary>Gemini <c>responseSchema</c>.</summary>
    public JsonObject ToGeminiSchema()
    {
        var node = new JsonObject { ["type"] = Type.ToUpperInvariant() };
        if (Nullable) node["nullable"] = true;
        if (Enum is { Count: > 0 }) node["enum"] = new JsonArray(Enum.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
        if (MaxLength is { } max) node["maxLength"] = max;
        if (Properties != null)
        {
            var props = new JsonObject();
            foreach (var (name, schema) in Properties) props[name] = schema.ToGeminiSchema();
            node["properties"] = props;
            node["required"] = new JsonArray(Properties.Select(p => (JsonNode)JsonValue.Create(p.Key)!).ToArray());
            node["propertyOrdering"] = new JsonArray(Properties.Select(p => (JsonNode)JsonValue.Create(p.Key)!).ToArray());
        }
        if (Items != null) node["items"] = Items.ToGeminiSchema();
        return node;
    }
}

/// <summary>One model call: fixed rules (system), the data as JSON text, the document as PDF or page images.</summary>
/// <param name="Pdf">The document as PDF; null when <paramref name="PageImages"/> carry it (PNG).</param>
public sealed record AiCallRequest(
    string System,
    string DataText,
    byte[]? Pdf,
    IReadOnlyList<byte[]> PageImages,
    AiSchema Schema,
    int MaxOutputTokens);

/// <summary>What the provider answered (any HTTP status); transport failures are thrown instead.</summary>
public sealed record AiProviderAnswer(
    int HttpStatus,
    string? ErrorType,
    string? ErrorMessage,
    string? StopReason,
    string? Text,
    long InputTokens,
    long OutputTokens,
    string? Model)
{
    public bool Success => HttpStatus is >= 200 and < 300;
}

public interface IAiProviderClient
{
    /// <summary>Provider name of the credentials response (<c>anthropic</c>, <c>google</c>).</summary>
    string Provider { get; }

    Task<AiProviderAnswer> SendAsync(AiProvider provider, AiCallRequest request, CancellationToken cancellationToken);
}

internal static class AiProviderHttp
{
    /// <summary>Named HttpClient for the providers; no timeout of its own, the gateway bounds each call.</summary>
    public const string HttpClientName = "AiProviders";

    /// <summary>Model ids go into a URL path: letters, digits and <c>.-_:@</c> only.</summary>
    private static readonly Regex ModelPattern = new("^[A-Za-z0-9._:@-]{1,120}$", RegexOptions.CultureInvariant);

    public static string Model(AiProvider provider) =>
        ModelPattern.IsMatch(provider.Model) ? provider.Model : throw new AiException(AiException.ProviderError, 502, "Model id refused");

    public static async Task<(int Status, JsonElement Body)> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            return ((int)response.StatusCode, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return ((int)response.StatusCode, default);
        }
    }

    public static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}

/// <summary>Anthropic Messages API: PDF as a <c>document</c> block, structured output by JSON schema.</summary>
public sealed class AnthropicProviderClient(IHttpClientFactory httpClients) : IAiProviderClient
{
    public const string Endpoint = "https://api.anthropic.com/v1/messages";
    public const string ApiVersion = "2023-06-01";

    public string Provider => "anthropic";

    public async Task<AiProviderAnswer> SendAsync(AiProvider provider, AiCallRequest request, CancellationToken cancellationToken)
    {
        var content = new JsonArray();
        if (request.Pdf != null)
        {
            content.Add(new JsonObject
            {
                ["type"] = "document",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "application/pdf", ["data"] = Convert.ToBase64String(request.Pdf) },
            });
        }
        foreach (var page in request.PageImages)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = Convert.ToBase64String(page) },
            });
        }
        content.Add(new JsonObject { ["type"] = "text", ["text"] = request.DataText });

        var body = new JsonObject
        {
            ["model"] = AiProviderHttp.Model(provider),
            ["max_tokens"] = request.MaxOutputTokens,
            ["system"] = request.System,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
            ["output_config"] = new JsonObject
            {
                ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = request.Schema.ToJsonSchema() },
            },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("x-api-key", provider.Credential.Value);
        message.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var (status, json) = await AiProviderHttp.SendAsync(httpClients.CreateClient(AiProviderHttp.HttpClientName), message, cancellationToken);
        if (status is < 200 or >= 300)
        {
            var error = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var e) ? e : default;
            return new AiProviderAnswer(status, AiProviderHttp.String(error, "type"), AiProviderHttp.String(error, "message"), null, null, 0, 0, provider.Model);
        }

        var text = new StringBuilder();
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in blocks.EnumerateArray())
            {
                if (AiProviderHttp.String(block, "type") == "text") text.Append(AiProviderHttp.String(block, "text"));
            }
        }
        var usage = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("usage", out var u) ? u : default;
        return new AiProviderAnswer(status, null, null, AiProviderHttp.String(json, "stop_reason"), text.ToString(),
            AiProviderHttp.Long(usage, "input_tokens"), AiProviderHttp.Long(usage, "output_tokens"),
            AiProviderHttp.String(json, "model") ?? provider.Model);
    }
}

/// <summary>Gemini <c>generateContent</c> with <c>responseSchema</c>; the key in <c>x-goog-api-key</c>, never the URL.</summary>
public sealed class GoogleProviderClient(IHttpClientFactory httpClients) : IAiProviderClient
{
    public const string EndpointBase = "https://generativelanguage.googleapis.com/v1beta/models/";

    public string Provider => "google";

    public async Task<AiProviderAnswer> SendAsync(AiProvider provider, AiCallRequest request, CancellationToken cancellationToken)
    {
        var parts = new JsonArray();
        if (request.Pdf != null)
        {
            parts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "application/pdf", ["data"] = Convert.ToBase64String(request.Pdf) } });
        }
        foreach (var page in request.PageImages)
        {
            parts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "image/png", ["data"] = Convert.ToBase64String(page) } });
        }
        parts.Add(new JsonObject { ["text"] = request.DataText });

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.System }) },
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = new JsonObject
            {
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = request.Schema.ToGeminiSchema(),
                ["temperature"] = 0,
                ["maxOutputTokens"] = request.MaxOutputTokens,
            },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{EndpointBase}{AiProviderHttp.Model(provider)}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("x-goog-api-key", provider.Credential.Value);

        var (status, json) = await AiProviderHttp.SendAsync(httpClients.CreateClient(AiProviderHttp.HttpClientName), message, cancellationToken);
        if (status is < 200 or >= 300)
        {
            var error = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var e) ? e : default;
            return new AiProviderAnswer(status, AiProviderHttp.String(error, "status"), AiProviderHttp.String(error, "message"), null, null, 0, 0, provider.Model);
        }

        string? finish = null;
        var text = new StringBuilder();
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("candidates", out var candidates)
            && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];
            finish = AiProviderHttp.String(candidate, "finishReason");
            if (candidate.TryGetProperty("content", out var c) && c.TryGetProperty("parts", out var ps) && ps.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in ps.EnumerateArray()) text.Append(AiProviderHttp.String(p, "text"));
            }
        }
        else if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("promptFeedback", out var feedback))
        {
            // The prompt itself was blocked: no candidate at all.
            finish = AiProviderHttp.String(feedback, "blockReason") is { } reason ? (reason == "SAFETY" ? "SAFETY" : "PROHIBITED_CONTENT") : null;
        }
        var usage = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("usageMetadata", out var u) ? u : default;
        return new AiProviderAnswer(status, null, null, finish, text.ToString(),
            AiProviderHttp.Long(usage, "promptTokenCount"), AiProviderHttp.Long(usage, "candidatesTokenCount"),
            AiProviderHttp.String(json, "modelVersion") ?? provider.Model);
    }
}
