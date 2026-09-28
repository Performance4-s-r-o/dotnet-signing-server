using DotNetSigningServer.Services.Backoffice.Documents;
using Microsoft.Extensions.Caching.Memory;

namespace DotNetSigningServer.Services.Legal;

/// <summary>
/// Docs module Shadow: pages are rendered exactly as with Off (hand-maintained rows, then
/// Razor), and in the background the service's version is fetched and differences in
/// version, title and effective date are logged. HTML is not compared — Markdig output and the
/// service's sanitized HTML always differ. Nothing is written to the database.
///
/// One comparison per document and locale per cache TTL; the request never waits for it.
/// </summary>
public sealed class ShadowLegalDocumentSource : ILegalDocumentSource
{
    private readonly DbLegalDocumentSource _database;
    private readonly BackofficeDocumentsCache _cache;
    private readonly LegalDocumentRefresher _refresher;
    private readonly IMemoryCache _memory;
    private readonly ILogger<ShadowLegalDocumentSource> _logger;

    public ShadowLegalDocumentSource(
        DbLegalDocumentSource database,
        BackofficeDocumentsCache cache,
        LegalDocumentRefresher refresher,
        IMemoryCache memory,
        ILogger<ShadowLegalDocumentSource> logger)
    {
        _database = database;
        _cache = cache;
        _refresher = refresher;
        _memory = memory;
        _logger = logger;
    }

    /// <summary>The comparison started by the last <see cref="GetAsync"/>, if any (tests await it).</summary>
    internal Task? PendingComparison { get; private set; }

    public async Task<LegalDocumentRendered?> GetAsync(string slug, string locale, CancellationToken cancellationToken = default)
    {
        var local = await _database.GetAsync(slug, locale, cancellationToken);
        try
        {
            var type = LegalSlugMap.TypeFor(slug);
            var serviceLocale = LegalLocales.Normalize(locale);
            var throttleKey = $"p4:doc-shadow:{type}:{serviceLocale}";
            if (type != null && !_memory.TryGetValue(throttleKey, out _))
            {
                _memory.Set(throttleKey, true, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = _cache.Ttl,
                    Size = 1,
                });
                PendingComparison = Task.Run(() => CompareAsync(type, serviceLocale, local));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[legal-docs] shadow comparison not started for {Slug}", slug);
        }
        return local;
    }

    private async Task CompareAsync(string type, string locale, LegalDocumentRendered? local)
    {
        try
        {
            var cached = _cache.Peek(type, locale);
            var remote = cached != null && _cache.IsFresh(cached)
                ? cached.Document
                : await _refresher.RefreshInBackground(type, locale, saveSnapshot: false);
            if (remote is null) return;

            var differences = LegalShadowDiff.Describe(local, remote);
            if (differences.Count == 0)
            {
                _logger.LogDebug("[legal-docs] shadow {Type}/{Locale}: local and service agree", type, locale);
            }
            else
            {
                _logger.LogInformation("[legal-docs] shadow {Type}/{Locale} differs: {Differences}",
                    type, locale, string.Join("; ", differences));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[legal-docs] shadow comparison of {Type}/{Locale} failed", type, locale);
        }
    }
}

/// <summary>What the shadow mode compares. Pure.</summary>
public static class LegalShadowDiff
{
    /// <summary>Human-readable differences in version, title, effective date and served locale; empty when they agree.</summary>
    public static IReadOnlyList<string> Describe(LegalDocumentRendered? local, BackofficeDocumentContent remote)
    {
        if (local is null)
        {
            return [$"local=static view, service=v{remote.Version}"];
        }

        var differences = new List<string>();
        if (local.Version != remote.Version)
        {
            differences.Add($"version local={local.Version} service={remote.Version}");
        }
        var remoteTitle = string.IsNullOrWhiteSpace(remote.Title) ? remote.Name : remote.Title;
        if (!string.Equals(local.Title.Trim(), remoteTitle.Trim(), StringComparison.Ordinal))
        {
            differences.Add($"title local=\"{local.Title}\" service=\"{remoteTitle}\"");
        }
        // Dates only: the manual rows carry a day, the service a moment of that day.
        var localDate = local.EffectiveFrom?.UtcDateTime.Date;
        var remoteDate = remote.EffectiveFrom?.UtcDateTime.Date;
        if (localDate != remoteDate)
        {
            differences.Add($"effective_from local={localDate:yyyy-MM-dd} service={remoteDate:yyyy-MM-dd}");
        }
        if (!string.Equals(local.Locale, remote.Locale, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add($"locale local={local.Locale} service={remote.Locale}");
        }
        return differences;
    }
}
