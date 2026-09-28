using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetSigningServer.Services.Backoffice.Documents;

/// <summary>Text of a legal document as served by <c>GET /v1/documents/{type}?format=html</c>.</summary>
/// <param name="Locale">Locale actually served (differs from the requested one when <paramref name="Fallback"/>).</param>
/// <param name="Html">Sanitized HTML; rendered as is.</param>
/// <param name="ContentHash">SHA-256 (hex) of <paramref name="Html"/>.</param>
public sealed record BackofficeDocumentContent(
    string Type,
    string Name,
    bool RequiresConsent,
    int Version,
    string Status,
    string ChangeKind,
    DateTimeOffset? EffectiveFrom,
    string? Summary,
    string Locale,
    bool Fallback,
    string Title,
    string Html,
    string ContentHash,
    string Url);

/// <summary>One version of a document (<c>DocumentVersionSummary</c>).</summary>
public sealed record BackofficeDocumentVersion(
    int Version,
    string Status,
    string ChangeKind,
    DateTimeOffset? EffectiveFrom,
    DateTimeOffset? PublishedAt,
    string? Summary);

/// <summary>A document of the product with the version in force and the upcoming one (<c>DocumentSummary</c>).</summary>
public sealed record BackofficeDocumentSummary(
    string Type,
    string Name,
    bool RequiresConsent,
    BackofficeDocumentVersion? Current,
    BackofficeDocumentVersion? Upcoming);

/// <summary>Result of a conditional <c>GET /v1/documents/{type}</c>.</summary>
/// <param name="Document">Null when the service answered <c>304 Not Modified</c>.</param>
public sealed record BackofficeDocumentFetch(BackofficeDocumentContent? Document, string? ETag)
{
    public bool NotModified => Document is null;
}

/// <summary>
/// Read-only client of the document endpoints of the P4 Backoffice service
/// (<c>openapi.json</c>: <c>/v1/documents</c>, <c>/v1/documents/{type}</c>,
/// <c>/v1/documents/{type}/versions</c>).
///
/// Vendored on purpose: the default build does not include <c>P4.Backoffice.Sdk</c> (see
/// <c>Directory.Build.props</c>), and tests run without it. The shapes follow the service's
/// OpenAPI document; the request/ETag handling mirrors the SDK's <c>DocumentsCache</c> 0.1.0.
/// TODO(P4.Backoffice.Sdk): once the package is restored in CI, replace the HTTP calls here
/// with the SDK's generated client and keep this class as the adapter — callers depend only
/// on the records above.
///
/// Every method throws <see cref="HttpRequestException"/> (or <see cref="TaskCanceledException"/>
/// on timeout) when the service fails; callers are background code or fall back to the snapshot.
/// </summary>
public sealed class BackofficeDocumentsClient
{
    /// <summary>Named HttpClient: service root, bearer key, <see cref="RequestTimeout"/>.</summary>
    public const string HttpClientName = "P4Backoffice.Documents";

    /// <summary>Same short timeout as the SDK: reads are never worth waiting for.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClients;

    public BackofficeDocumentsClient(IHttpClientFactory httpClients)
    {
        _httpClients = httpClients;
    }

    /// <summary>The version in force of <paramref name="type"/> in <paramref name="locale"/> (with the service's language fallback).</summary>
    public async Task<BackofficeDocumentFetch> GetDocumentAsync(string type, string locale, string? etag, CancellationToken cancellationToken)
    {
        var url = $"v1/documents/{Uri.EscapeDataString(type)}?format=html&locale={Uri.EscapeDataString(locale)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (etag != null && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        using var response = await Http().SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified && etag != null)
        {
            return new BackofficeDocumentFetch(null, etag);
        }
        response.EnsureSuccessStatusCode();

        var dto = await ReadAsync<DocumentContentDto>(response, cancellationToken);
        if (dto.Version <= 0 || string.IsNullOrEmpty(dto.Type))
        {
            throw new HttpRequestException("Backoffice document response is missing its type or version");
        }

        var document = new BackofficeDocumentContent(
            dto.Type, dto.Name ?? "", dto.RequiresConsent, dto.Version, dto.Status ?? "", dto.ChangeKind ?? "",
            dto.EffectiveFrom, dto.Summary, dto.Locale ?? locale, dto.Fallback, dto.Title ?? "",
            dto.Html ?? "", dto.ContentHash ?? "", dto.Url ?? "");
        return new BackofficeDocumentFetch(document, response.Headers.ETag?.ToString());
    }

    /// <summary><c>GET /v1/documents</c>: every document of the product.</summary>
    public async Task<IReadOnlyList<BackofficeDocumentSummary>> ListAsync(CancellationToken cancellationToken)
    {
        using var response = await Http().GetAsync("v1/documents", cancellationToken);
        response.EnsureSuccessStatusCode();
        var dto = await ReadAsync<DocumentListDto>(response, cancellationToken);
        return (dto.Data ?? [])
            .Where(d => !string.IsNullOrEmpty(d.Type))
            .Select(d => new BackofficeDocumentSummary(d.Type!, d.Name ?? "", d.RequiresConsent, Map(d.Current), Map(d.Upcoming)))
            .ToList();
    }

    /// <summary><c>GET /v1/documents/{type}/versions</c>: version history, newest first.</summary>
    public async Task<IReadOnlyList<BackofficeDocumentVersion>> VersionsAsync(string type, CancellationToken cancellationToken)
    {
        using var response = await Http().GetAsync($"v1/documents/{Uri.EscapeDataString(type)}/versions", cancellationToken);
        response.EnsureSuccessStatusCode();
        var dto = await ReadAsync<VersionListDto>(response, cancellationToken);
        return (dto.Data ?? []).Select(Map).OfType<BackofficeDocumentVersion>().ToList();
    }

    private HttpClient Http() => _httpClients.CreateClient(HttpClientName);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(body, Json, cancellationToken)
                   ?? throw new HttpRequestException("Backoffice document response is empty");
        }
        catch (JsonException ex)
        {
            // One exception type for "the service did not give us a usable answer".
            throw new HttpRequestException("Backoffice document response is not valid JSON", ex);
        }
    }

    private static BackofficeDocumentVersion? Map(VersionDto? v) =>
        v is null || v.Version <= 0
            ? null
            : new BackofficeDocumentVersion(v.Version, v.Status ?? "", v.ChangeKind ?? "", v.EffectiveFrom, v.PublishedAt, v.Summary);

    // Wire shapes (snake_case). Only the fields this product uses.

    private sealed class DocumentContentDto
    {
        public string Type { get; set; } = "";
        public string? Name { get; set; }
        public bool RequiresConsent { get; set; }
        public int Version { get; set; }
        public string? Status { get; set; }
        public string? ChangeKind { get; set; }
        public DateTimeOffset? EffectiveFrom { get; set; }
        public string? Summary { get; set; }
        public string? Locale { get; set; }
        public bool Fallback { get; set; }
        public string? Title { get; set; }
        public string? Html { get; set; }
        public string? ContentHash { get; set; }
        public string? Url { get; set; }
    }

    private sealed class VersionDto
    {
        public int Version { get; set; }
        public string? Status { get; set; }
        public string? ChangeKind { get; set; }
        public DateTimeOffset? EffectiveFrom { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public string? Summary { get; set; }
    }

    private sealed class DocumentSummaryDto
    {
        public string? Type { get; set; }
        public string? Name { get; set; }
        public bool RequiresConsent { get; set; }
        public VersionDto? Current { get; set; }
        public VersionDto? Upcoming { get; set; }
    }

    private sealed class DocumentListDto
    {
        [JsonPropertyName("data")]
        public List<DocumentSummaryDto>? Data { get; set; }
    }

    private sealed class VersionListDto
    {
        [JsonPropertyName("data")]
        public List<VersionDto>? Data { get; set; }
    }
}
