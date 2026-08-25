using DotNetSigningServer.Data;
using Markdig;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services;

/// <summary>
/// Reads the currently effective version of a legal document from this
/// platform's own database and renders its Markdown to HTML.
///
/// Rows are edited by hand directly in the database, so results are not cached —
/// an edit shows up on the next request. The query is a single indexed lookup
/// against a table with a handful of rows.
/// </summary>
public class LegalDocumentService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<LegalDocumentService> _logger;
    private readonly MarkdownPipeline _pipeline;

    public LegalDocumentService(
        ApplicationDbContext dbContext,
        ILogger<LegalDocumentService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseAutoLinks()
            // The rendered output is emitted with @Html.Raw. Markdig passes raw
            // inline HTML through by default, so disable it: a stray <script> in
            // a hand-edited row would otherwise become stored XSS.
            .DisableHtml()
            .Build();
    }

    /// <summary>
    /// Returns the newest non-draft version of (slug, locale) whose
    /// EffectiveFrom has already passed, or <c>null</c> when there is none.
    /// Never throws — callers fall back to the static Razor view.
    /// </summary>
    public async Task<LegalDocumentRendered?> TryGetAsync(
        string slug,
        string locale,
        CancellationToken ct = default)
    {
        var normalisedLocale = string.Equals(locale, "cs", StringComparison.OrdinalIgnoreCase)
            ? "cs"
            : "en";
        var now = DateTimeOffset.UtcNow;

        try
        {
            var document = await _dbContext.LegalDocuments
                .AsNoTracking()
                .Where(d => d.Slug == slug
                            && d.Locale == normalisedLocale
                            && !d.IsDraft
                            && d.EffectiveFrom <= now)
                .OrderByDescending(d => d.EffectiveFrom)
                .ThenByDescending(d => d.Version)
                .FirstOrDefaultAsync(ct);

            if (document is null || string.IsNullOrWhiteSpace(document.Content))
            {
                return null;
            }

            return new LegalDocumentRendered(
                Slug: document.Slug,
                Locale: document.Locale,
                Version: document.Version,
                Title: document.Title,
                Summary: document.Summary,
                EffectiveFrom: document.EffectiveFrom,
                ContentHtml: Markdown.ToHtml(document.Content, _pipeline));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[legal-docs] lookup failed for {Slug}/{Locale}", slug, normalisedLocale);
            return null;
        }
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
