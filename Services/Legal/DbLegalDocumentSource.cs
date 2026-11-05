using DotNetSigningServer.Data;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Legal;

/// <summary>
/// Reads the effective version of a legal document from the snapshot in the
/// <c>LegalDocuments</c> table: the last successful answer of the P4 Backoffice service
/// (<see cref="LegalDocumentsSnapshotWriter"/>). The table holds no texts of its own —
/// a row without service HTML is ignored.
///
/// Used as the fallback of <see cref="BackofficeLegalDocumentSource"/> and, with the Docs
/// module Off, as the only source. Not cached, so a new snapshot shows on the next request;
/// the query is a single indexed lookup.
/// </summary>
public class DbLegalDocumentSource : ILegalDocumentSource
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<DbLegalDocumentSource> _logger;
    private readonly TimeProvider _time;

    public DbLegalDocumentSource(
        ApplicationDbContext dbContext,
        ILogger<DbLegalDocumentSource> logger,
        TimeProvider? time = null)
    {
        _dbContext = dbContext;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<LegalDocumentRendered?> GetAsync(string slug, string locale, CancellationToken cancellationToken = default)
    {
        var normalised = LegalLocales.Normalize(locale);
        var rendered = await TryGetAsync(slug, normalised, cancellationToken);
        if (rendered is null && normalised != LegalLocales.Default)
        {
            rendered = await TryGetAsync(slug, LegalLocales.Default, cancellationToken);
        }
        return rendered;
    }

    /// <summary>
    /// The newest non-draft snapshot of (slug, locale) whose EffectiveFrom has passed and that
    /// has service HTML; null when there is none. Never throws.
    /// </summary>
    public async Task<LegalDocumentRendered?> TryGetAsync(string slug, string locale, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        try
        {
            var document = await _dbContext.LegalDocuments
                .AsNoTracking()
                .Where(d => d.Slug == slug
                            && d.Locale == locale
                            && !d.IsDraft
                            && d.EffectiveFrom <= now
                            && d.ContentHtml != null
                            && d.ContentHtml != "")
                .OrderByDescending(d => d.EffectiveFrom)
                .ThenByDescending(d => d.Version)
                .FirstOrDefaultAsync(cancellationToken);

            return document is null
                ? null
                : new LegalDocumentRendered(
                    Slug: document.Slug,
                    Locale: document.Locale,
                    Version: document.Version,
                    Title: document.Title,
                    Summary: document.Summary,
                    EffectiveFrom: document.EffectiveFrom,
                    ContentHtml: document.ContentHtml!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[legal-docs] lookup failed for {Slug}/{Locale}", slug, locale);
            return null;
        }
    }
}
