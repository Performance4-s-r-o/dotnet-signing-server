using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Markdig;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Legal;

/// <summary>
/// Reads the effective version of a legal document from this platform's own
/// <c>LegalDocuments</c> table.
///
/// Hand-maintained rows (<c>Source = manual</c>) hold Markdown, rendered with Markdig. With
/// <c>includeSnapshots</c> (Docs module On, this class is then the fallback of the service)
/// the service's snapshot is read too: rows with <c>Source = backoffice</c>, and the service
/// HTML stored next to a manual row of the same version. Without it (Off, Shadow) only the
/// manual rows and their Markdown are used — exactly the behaviour before the integration.
///
/// Rows are edited by hand directly in the database, so results are not cached — an edit
/// shows up on the next request. The query is a single indexed lookup.
/// </summary>
public class DbLegalDocumentSource : ILegalDocumentSource
{
    /// <summary>
    /// The rendered output is emitted with @Html.Raw. Markdig passes raw inline HTML through
    /// by default, so it is disabled: a stray &lt;script&gt; in a hand-edited row would
    /// otherwise become stored XSS. <c>LegalDocumentMarkdownTests</c> guards this.
    /// </summary>
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<DbLegalDocumentSource> _logger;
    private readonly TimeProvider _time;

    public DbLegalDocumentSource(
        ApplicationDbContext dbContext,
        ILogger<DbLegalDocumentSource> logger,
        bool includeSnapshots = false,
        TimeProvider? time = null)
    {
        _dbContext = dbContext;
        _logger = logger;
        IncludeSnapshots = includeSnapshots;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Whether the service snapshot is read as well (Docs module On).</summary>
    public bool IncludeSnapshots { get; }

    public async Task<LegalDocumentRendered?> GetAsync(string slug, string locale, CancellationToken cancellationToken = default)
    {
        var normalised = LegalLocales.Normalize(locale);
        var rendered = await TryGetAsync(slug, normalised, cancellationToken);
        if (rendered is null && normalised != LegalLocales.Default)
        {
            // English is the platform-default locale and the most likely to be authored —
            // fall back to it before resorting to the static view.
            rendered = await TryGetAsync(slug, LegalLocales.Default, cancellationToken);
        }
        return rendered;
    }

    /// <summary>
    /// The newest non-draft row of (slug, locale) whose EffectiveFrom has passed and that has
    /// a text; null when there is none. Never throws.
    /// </summary>
    public async Task<LegalDocumentRendered?> TryGetAsync(string slug, string locale, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        try
        {
            var query = _dbContext.LegalDocuments
                .AsNoTracking()
                .Where(d => d.Slug == slug
                            && d.Locale == locale
                            && !d.IsDraft
                            && d.EffectiveFrom <= now);
            if (!IncludeSnapshots)
            {
                query = query.Where(d => d.Source == LegalDocumentSources.Manual);
            }

            // With the snapshot a few rows, not one: the newest may be a snapshot-less manual
            // row of a newer version. Without it exactly the one row read before the integration.
            var candidates = await query
                .OrderByDescending(d => d.EffectiveFrom)
                .ThenByDescending(d => d.Version)
                .Take(IncludeSnapshots ? 5 : 1)
                .ToListAsync(cancellationToken);

            foreach (var document in candidates)
            {
                var html = Render(document);
                if (html != null)
                {
                    return new LegalDocumentRendered(
                        Slug: document.Slug,
                        Locale: document.Locale,
                        Version: document.Version,
                        Title: document.Title,
                        Summary: document.Summary,
                        EffectiveFrom: document.EffectiveFrom,
                        ContentHtml: html);
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[legal-docs] lookup failed for {Slug}/{Locale}", slug, locale);
            return null;
        }
    }

    private string? Render(LegalDocument document)
    {
        // The service's HTML is sanitized there and served as is; it wins when the snapshot is in use.
        if (IncludeSnapshots && !string.IsNullOrWhiteSpace(document.ContentHtml))
        {
            return document.ContentHtml;
        }
        if (document.Source == LegalDocumentSources.Manual && !string.IsNullOrWhiteSpace(document.Content))
        {
            return Markdown.ToHtml(document.Content, Pipeline);
        }
        return null;
    }
}
