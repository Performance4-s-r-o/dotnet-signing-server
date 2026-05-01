using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetSigningServer.Options;
using Markdig;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services;

/// <summary>
/// Fetches the currently effective version of a legal document from the
/// v0 CMS (`GET /api/legal-documents/{slug}?locale={cs|en}`), renders the
/// returned Markdown to HTML once, and caches the result.
/// </summary>
public class LegalDocumentsClient
{
    private readonly HttpClient _httpClient;
    private readonly LegalDocumentsCmsOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<LegalDocumentsClient> _logger;
    private readonly MarkdownPipeline _pipeline;

    public LegalDocumentsClient(
        HttpClient httpClient,
        IOptions<LegalDocumentsCmsOptions> options,
        IMemoryCache cache,
        ILogger<LegalDocumentsClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _cache = cache;
        _logger = logger;
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseAutoLinks()
            .Build();
    }

    /// <summary>
    /// Returns the rendered document for (slug, locale) or <c>null</c> when
    /// the CMS is not configured, the document doesn't exist or any error
    /// occurs. Never throws — callers should fall back to a static view.
    /// </summary>
    public async Task<LegalDocumentRendered?> TryGetAsync(
        string slug,
        string locale,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return null;
        }

        var normalisedLocale = string.Equals(locale, "cs", StringComparison.OrdinalIgnoreCase)
            ? "cs"
            : "en";
        var cacheKey = $"legal::{slug}::{normalisedLocale}";

        if (_cache.TryGetValue(cacheKey, out LegalDocumentRendered? cached))
        {
            return cached;
        }

        try
        {
            var url = $"{_options.BaseUrl!.TrimEnd('/')}/api/legal-documents/{Uri.EscapeDataString(slug)}?locale={normalisedLocale}";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            using var response = await _httpClient.GetAsync(url, cts.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Cache the negative answer briefly to avoid hammering the CMS
                // for documents that simply aren't in it.
                _cache.Set(cacheKey, (LegalDocumentRendered?)null, TimeSpan.FromSeconds(Math.Min(60, _options.CacheSeconds)));
                return null;
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var payload = await JsonSerializer.DeserializeAsync<DocumentEnvelope>(stream, JsonOpts, cts.Token);

            if (payload?.Document is null || string.IsNullOrWhiteSpace(payload.Document.Content))
            {
                return null;
            }

            var rendered = new LegalDocumentRendered(
                Slug: payload.Document.Slug,
                Locale: payload.Document.Locale,
                Version: payload.Document.Version,
                Title: payload.Document.Title,
                Summary: payload.Document.Summary,
                EffectiveFrom: payload.Document.EffectiveFrom,
                ContentHtml: Markdown.ToHtml(payload.Document.Content, _pipeline));

            _cache.Set(cacheKey, rendered, TimeSpan.FromSeconds(_options.CacheSeconds));
            return rendered;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[legal-docs] CMS fetch failed for {Slug}/{Locale}", slug, normalisedLocale);
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class DocumentEnvelope
    {
        [JsonPropertyName("document")]
        public DocumentPayload? Document { get; set; }
    }

    private sealed class DocumentPayload
    {
        [JsonPropertyName("slug")] public string Slug { get; set; } = "";
        [JsonPropertyName("locale")] public string Locale { get; set; } = "";
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("content")] public string Content { get; set; } = "";
        [JsonPropertyName("effective_from")] public DateTimeOffset EffectiveFrom { get; set; }
    }
}

public record LegalDocumentRendered(
    string Slug,
    string Locale,
    int Version,
    string Title,
    string? Summary,
    DateTimeOffset EffectiveFrom,
    string ContentHtml);
