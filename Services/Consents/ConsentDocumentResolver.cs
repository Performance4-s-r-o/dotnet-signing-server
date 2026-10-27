using DotNetSigningServer.Data;
using DotNetSigningServer.Services.Backoffice.Documents;
using DotNetSigningServer.Services.Legal;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Consents;

/// <summary>A document as a consent form shows it.</summary>
/// <param name="Slug">Route slug of <c>/Legal/…</c>; null for a document this product does not show.</param>
/// <param name="Version">Version in force (what the reader of <c>/Legal/…</c> gets).</param>
/// <param name="Locale">Language of that text (<c>en</c> or <c>cs</c>).</param>
/// <param name="ContentHash">The service's SHA-256 of that text; null when not known locally.</param>
/// <param name="Since">When <paramref name="Version"/> came into force; null when unknown.</param>
public sealed record ConsentDocumentVersion(
    string Document,
    string Action,
    string? Slug,
    int Version,
    string Locale,
    string? ContentHash,
    DateTimeOffset? Since,
    string? Title,
    string? Summary);

/// <summary>
/// Which version of each consent document a form shows, from local data only — never from the
/// service: the text a reader of <c>/Legal/…</c> gets (<see cref="ILegalDocumentSource"/>: the
/// service's cached copy, the <c>LegalDocuments</c> snapshot or hand-maintained rows), then
/// <c>docs:meta</c>, then version 1 for a document with a static page. A document with none of these
/// throws <see cref="ConsentVersionUnknownException"/> (no consent is recorded with a made-up version).
/// </summary>
public sealed class ConsentDocumentResolver
{
    /// <summary>The static Razor page of a legal document, imported into the service as v1.</summary>
    public const int StaticPageVersion = 1;

    private readonly ApplicationDbContext _db;
    private readonly ILegalDocumentSource _documents;
    private readonly BackofficeDocumentsCache? _cache;
    private readonly ILogger<ConsentDocumentResolver> _logger;

    public ConsentDocumentResolver(
        ApplicationDbContext db,
        ILegalDocumentSource documents,
        IServiceProvider services,
        ILogger<ConsentDocumentResolver> logger)
    {
        _db = db;
        _documents = documents;
        // Registered only when the Docs module is Shadow or On.
        _cache = services.GetService<BackofficeDocumentsCache>();
        _logger = logger;
    }

    public async Task<IReadOnlyList<ConsentDocumentVersion>> ResolveAsync(
        IEnumerable<ConsentRequirement> requirements, string uiLocale, CancellationToken cancellationToken = default)
    {
        DocumentsMeta? meta = null;
        try
        {
            meta = await DocumentsMetaUpdater.ReadAsync(_db, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[consents] docs:meta unavailable; using the legal documents only");
        }

        var result = new List<ConsentDocumentVersion>();
        foreach (var requirement in requirements)
        {
            var slug = LegalSlugMap.SlugFor(requirement.Document);
            var shown = slug is null ? null : await _documents.GetAsync(slug, uiLocale, cancellationToken);
            var entry = meta?.Documents.GetValueOrDefault(requirement.Document);

            // Version 1 is the static Razor page, which is what /Legal/<slug> shows when nothing
            // else is published. A document without a page and without docs:meta has no version
            // we could honestly name: refuse instead of inventing one.
            var version = shown?.Version ?? entry?.CurrentVersion ?? (slug is null ? (int?)null : StaticPageVersion)
                ?? throw new ConsentVersionUnknownException(requirement.Document);
            var locale = shown?.Locale ?? LegalLocales.Default;
            var fromMeta = entry?.CurrentVersion == version;
            result.Add(new ConsentDocumentVersion(
                requirement.Document,
                requirement.Action,
                slug,
                version,
                locale,
                await HashAsync(requirement.Document, locale, version, cancellationToken),
                fromMeta && entry!.CurrentSince is { } since ? since : shown?.EffectiveFrom,
                shown?.Title,
                shown?.Summary ?? (fromMeta ? entry!.CurrentSummary : null)));
        }
        return result;
    }

    /// <summary>
    /// The service's hash of (document, locale, version): the in-memory copy, then the
    /// <c>LegalDocuments</c> snapshot. Null when neither has it (the service fills it in).
    /// </summary>
    public async Task<string?> HashAsync(string document, string locale, int version, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_cache?.Peek(document, locale) is { } cached
                && cached.Document.Version == version
                && cached.Document.Locale == locale
                && LegalDocumentsSnapshotWriter.NormaliseHash(cached.Document.ContentHash) is { } fromCache)
            {
                return fromCache;
            }

            var slug = LegalSlugMap.SlugFor(document);
            if (slug is null) return null;
            var stored = await _db.LegalDocuments.AsNoTracking()
                .Where(d => d.Slug == slug && d.Locale == locale && d.Version == version && d.ContentHash != null)
                .Select(d => d.ContentHash)
                .FirstOrDefaultAsync(cancellationToken);
            return LegalDocumentsSnapshotWriter.NormaliseHash(stored);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[consents] content hash of {Document}/{Locale} v{Version} unavailable", document, locale, version);
            return null;
        }
    }
}

/// <summary>No local source names a version of a consent document, so no consent can be recorded for it.</summary>
public sealed class ConsentVersionUnknownException(string document)
    : InvalidOperationException($"No version of the consent document '{document}' is known (no published text, docs:meta or static page).")
{
    public string Document { get; } = document;
}
