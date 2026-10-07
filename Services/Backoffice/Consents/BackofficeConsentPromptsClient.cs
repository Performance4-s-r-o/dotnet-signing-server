using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetSigningServer.Services.Backoffice.Consents;

/// <summary>One piece of a consent sentence: plain text, or a link to a legal document.</summary>
/// <remarks>
/// Segments, never HTML: the service decides the wording, this app decides the markup. A link
/// carries the document it points at and the version in force, so the sentence and the document
/// can never drift apart.
/// </remarks>
public abstract record ConsentPromptSegment
{
    public sealed record Text(string Value) : ConsentPromptSegment;

    public sealed record Link(string DocumentType, string Title, string Url, int Version, string ContentHash) : ConsentPromptSegment;
}

/// <summary>
/// The wording of one consent as the service publishes it
/// (<c>GET /v1/consent-prompts</c>). <paramref name="PromptVersion"/> and
/// <paramref name="PromptHash"/> go back with the consent, so what the person was shown is
/// recorded with what they agreed to.
/// </summary>
/// <param name="Locale">Locale actually served; <paramref name="Fallback"/> when it is not the requested one.</param>
public sealed record BackofficeConsentPrompt(
    string Key,
    string Purpose,
    string Context,
    bool Required,
    int PromptVersion,
    string PromptHash,
    string Locale,
    string? RequestedLocale,
    bool Fallback,
    IReadOnlyList<ConsentPromptSegment> Segments)
{
    /// <summary>Documents the sentence links to — the ones a consent may name this prompt for.</summary>
    public IReadOnlySet<string> Documents =>
        Segments.OfType<ConsentPromptSegment.Link>().Select(l => l.DocumentType).ToHashSet(StringComparer.Ordinal);
}

/// <summary>What the service answered, with the ETag to revalidate with.</summary>
/// <param name="Prompts">Null when the service answered <c>304 Not Modified</c>.</param>
public sealed record BackofficeConsentPromptsFetch(IReadOnlyList<BackofficeConsentPrompt>? Prompts, string? ETag)
{
    public bool NotModified => Prompts is null;
}

/// <summary>
/// <c>GET /v1/consent-prompts?locale=&amp;context=</c>: the sentences next to the consent
/// checkboxes, written and versioned in the backoffice service instead of in this app's
/// resources.
///
/// Own client of the service's OpenAPI document (no SDK dependency, so every build — forks
/// included — can use it), mirroring <see cref="Documents.BackofficeDocumentsClient"/>.
///
/// Throws <see cref="HttpRequestException"/> (or <see cref="TaskCanceledException"/> on timeout)
/// when the service fails; callers serve the cached copy.
/// </summary>
public sealed class BackofficeConsentPromptsClient
{
    /// <summary>Named HttpClient: service root, bearer key, <see cref="RequestTimeout"/>.</summary>
    public const string HttpClientName = "P4Backoffice.ConsentPrompts";

    /// <summary>As short as the documents client: a page never waits for this.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClients;

    public BackofficeConsentPromptsClient(IHttpClientFactory httpClients)
    {
        _httpClients = httpClients;
    }

    /// <summary>Prompts of one context (<c>registration</c>, …) in one locale.</summary>
    public async Task<BackofficeConsentPromptsFetch> GetPromptsAsync(string locale, string context, string? etag, CancellationToken cancellationToken)
    {
        var url = $"v1/consent-prompts?locale={Uri.EscapeDataString(locale)}&context={Uri.EscapeDataString(context)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (etag != null && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        using var response = await Http().SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified && etag != null)
        {
            return new BackofficeConsentPromptsFetch(null, etag);
        }
        response.EnsureSuccessStatusCode();

        var dto = await ReadAsync<PromptListDto>(response, cancellationToken);
        var prompts = (dto.Data ?? []).Select(Map).OfType<BackofficeConsentPrompt>().ToList();
        return new BackofficeConsentPromptsFetch(prompts, response.Headers.ETag?.ToString());
    }

    private HttpClient Http() => _httpClients.CreateClient(HttpClientName);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(body, Json, cancellationToken)
                   ?? throw new HttpRequestException("Backoffice consent prompts response is empty");
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("Backoffice consent prompts response is not valid JSON", ex);
        }
    }

    /// <summary>Null for a prompt that could not be rendered; one bad entry must not lose the rest.</summary>
    private static BackofficeConsentPrompt? Map(PromptDto dto)
    {
        if (string.IsNullOrEmpty(dto.Key) || dto.PromptVersion <= 0 || string.IsNullOrEmpty(dto.PromptHash)) return null;
        var segments = (dto.Segments ?? []).Select(MapSegment).OfType<ConsentPromptSegment>().ToList();
        if (segments.Count == 0) return null;
        return new BackofficeConsentPrompt(
            dto.Key!, dto.Purpose ?? dto.Key!, dto.Context ?? "", dto.Required, dto.PromptVersion, dto.PromptHash!,
            dto.Locale ?? "", dto.RequestedLocale, dto.Fallback, segments);
    }

    private static ConsentPromptSegment? MapSegment(SegmentDto dto) => dto.Type switch
    {
        "text" when !string.IsNullOrEmpty(dto.Value) => new ConsentPromptSegment.Text(dto.Value!),
        "link" when !string.IsNullOrEmpty(dto.DocumentType) && !string.IsNullOrEmpty(dto.Url) =>
            new ConsentPromptSegment.Link(dto.DocumentType!, dto.Title ?? dto.DocumentType!, dto.Url!, dto.Version, dto.ContentHash ?? ""),
        _ => null,
    };

    private sealed record PromptListDto([property: JsonPropertyName("data")] List<PromptDto>? Data);

    private sealed record PromptDto(
        string? Key,
        string? Purpose,
        string? Context,
        bool Required,
        int PromptVersion,
        string? PromptHash,
        string? Locale,
        string? RequestedLocale,
        bool Fallback,
        List<SegmentDto>? Segments);

    private sealed record SegmentDto(
        string? Type,
        string? Value,
        string? DocumentType,
        string? Title,
        string? Url,
        int Version,
        string? ContentHash);
}
