using DotNetSigningServer.Services.Backoffice.Documents;

namespace DotNetSigningServer.Services.Legal;

/// <summary>
/// Docs module On: the P4 Backoffice service is the source of the texts.
///
/// A page reads the in-memory copy of the service's answer (<see cref="BackofficeDocumentsCache"/>);
/// when it is stale, a revalidation starts in the background and the stale copy is shown.
/// Without a copy in memory (cold start, outage) the page reads the snapshot in
/// <c>LegalDocuments</c> through <see cref="DbLegalDocumentSource"/> (the requested locale, then
/// English) and a fetch starts in the background; with an empty snapshot the result is null and
/// the controller renders the static Razor view. The request never waits for the service and
/// this never throws.
/// </summary>
public sealed class BackofficeLegalDocumentSource : ILegalDocumentSource
{
    private readonly BackofficeDocumentsCache _cache;
    private readonly LegalDocumentRefresher _refresher;
    private readonly DbLegalDocumentSource _database;
    private readonly ILogger<BackofficeLegalDocumentSource> _logger;

    public BackofficeLegalDocumentSource(
        BackofficeDocumentsCache cache,
        LegalDocumentRefresher refresher,
        DbLegalDocumentSource database,
        ILogger<BackofficeLegalDocumentSource> logger)
    {
        _cache = cache;
        _refresher = refresher;
        _database = database;
        _logger = logger;
    }

    /// <summary>The background fetch started by the last <see cref="GetAsync"/>, if any (tests await it).</summary>
    internal Task<BackofficeDocumentContent?>? PendingRefresh { get; private set; }

    public async Task<LegalDocumentRendered?> GetAsync(string slug, string locale, CancellationToken cancellationToken = default)
    {
        var type = LegalSlugMap.TypeFor(slug);
        string? refreshLocale = null;
        if (type != null)
        {
            var serviceLocale = LegalLocales.Normalize(locale);
            try
            {
                var cached = _cache.Peek(type, serviceLocale);
                var stale = cached is null || !_cache.IsFresh(cached);
                if (cached != null && !string.IsNullOrWhiteSpace(cached.Document.Html))
                {
                    if (stale) StartRefresh(type, serviceLocale);
                    return Render(slug, cached.Document);
                }
                if (stale) refreshLocale = serviceLocale;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[legal-docs] reading the service copy of {Type}/{Locale} failed; using the snapshot", type, serviceLocale);
            }
        }

        // Read the snapshot before the background fetch starts, so this request sees the snapshot
        // as it was and never races the fetch writing a new one.
        var snapshot = await _database.GetAsync(slug, locale, cancellationToken);
        if (type != null && refreshLocale != null) StartRefresh(type, refreshLocale);
        return snapshot;
    }

    private void StartRefresh(string type, string serviceLocale)
    {
        try
        {
            PendingRefresh = _refresher.RefreshInBackground(type, serviceLocale, saveSnapshot: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[legal-docs] starting the refresh of {Type}/{Locale} failed", type, serviceLocale);
        }
    }

    internal static LegalDocumentRendered Render(string slug, BackofficeDocumentContent document) => new(
        Slug: slug,
        Locale: document.Locale,
        Version: document.Version,
        Title: string.IsNullOrWhiteSpace(document.Title) ? document.Name : document.Title,
        Summary: document.Summary,
        EffectiveFrom: document.EffectiveFrom,
        ContentHtml: document.Html);
}
