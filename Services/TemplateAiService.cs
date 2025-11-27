using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace DotNetSigningServer.Services;

public class TemplateAiService
{
    private readonly HttpClient _httpClient;
    private readonly AiOptions _options;
    private readonly ILogger<TemplateAiService> _logger;

    public TemplateAiService(HttpClient httpClient, IOptions<AiOptions> options, ILogger<TemplateAiService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled =>
        _options.Enabled &&
        string.Equals(_options.Provider, "google", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(_options.Google?.ApiKey);

    public async Task<IReadOnlyList<PdfFieldDefinition>> DetectFieldsAsync(string pdfBase64, string? prompt, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("AI detection is not configured.");
        }

        if (string.IsNullOrWhiteSpace(pdfBase64))
        {
            return Array.Empty<PdfFieldDefinition>();
        }

        var apiKey = _options.Google.ApiKey!;
        var model = _options.Google.Model ?? "gemini-1.5-flash";
        var endpointBase = _options.Google.Endpoint?.TrimEnd('/') ?? "https://generativelanguage.googleapis.com/v1beta";
        var url = $"{endpointBase}/models/{model}:generateContent?key={apiKey}";

        var systemPrompt = @"You extract form field coordinates from a PDF. Return a compact JSON array of fields with:
[
  {""fieldName"":""Field_1"",""page"":1,""rect"":{""x"":10,""y"":20,""width"":150,""height"":30},""type"":""text""}
]
Rules:
- Coordinates use PDF points, origin bottom-left. y is distance from bottom.
- width/height in points.
- page starts at 1.
- Supported types: text, image, signature, barcode.
- Do not include extra text, code fences, or explanations. Only JSON.";

        var request = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = $"{systemPrompt}\n\nUser request: {prompt ?? "Detect fields automatically."}" },
                        new
                        {
                            inline_data = new
                            {
                                mime_type = "application/pdf",
                                data = pdfBase64
                            }
                        }
                    }
                }
            }
        };

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
            };

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var text = ExtractTextResponse(payload);
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogWarning("AI response contained no text payload.");
                return Array.Empty<PdfFieldDefinition>();
            }

            var json = ExtractJson(text);
            var fields = JsonSerializer.Deserialize<List<PdfFieldDefinition>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new();

            // Filter out invalid items
            return fields
                .Where(f => f.Rect != null)
                .Where(f => f.Rect.Width > 0 && f.Rect.Height > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI detection failed");
            throw;
        }
    }

    private static string? ExtractTextResponse(JsonElement payload)
    {
        if (!payload.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            return null;
        }

        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var content)) return null;
        if (!content.TryGetProperty("parts", out var parts) || parts.GetArrayLength() == 0) return null;
        var firstPart = parts[0];

        if (firstPart.TryGetProperty("text", out var textNode))
        {
            return textNode.GetString();
        }

        return firstPart.ToString();
    }

    private static string ExtractJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
        {
            var start = trimmed.IndexOf('\n');
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start)
            {
                trimmed = trimmed.Substring(start + 1, end - start - 1).Trim();
            }
        }

        return trimmed;
    }
}
