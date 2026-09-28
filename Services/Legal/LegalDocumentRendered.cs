namespace DotNetSigningServer.Services.Legal;

/// <summary>A legal document ready for <c>Views/Legal/Dynamic.cshtml</c>.</summary>
/// <param name="Locale">Language of the text actually shown (may differ from the page language).</param>
/// <param name="ContentHtml">Safe HTML: Markdig output with raw HTML disabled, or the service's sanitized HTML.</param>
public record LegalDocumentRendered(
    string Slug,
    string Locale,
    int Version,
    string Title,
    string? Summary,
    DateTimeOffset? EffectiveFrom,
    string ContentHtml);
