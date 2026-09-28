using Microsoft.Extensions.Caching.Memory;

namespace DotNetSigningServer.Services.Backoffice.Documents;

/// <summary>A document held in memory with the ETag it was served with.</summary>
public sealed record CachedBackofficeDocument(BackofficeDocumentContent Document, string? ETag, DateTimeOffset FetchedAt);

/// <summary>
/// In-memory copy of the service's documents, keyed like the SDK's <c>DocumentsCache</c>
/// (<c>p4:doc:{type}:{locale}</c>): an entry is fresh for <see cref="Ttl"/>, revalidated with
/// <c>If-None-Match</c> after that, and kept for 7 days (sliding) as the stale copy served
/// during an outage.
///
/// Unlike the SDK class, reading (<see cref="Peek"/>) and revalidating
/// (<see cref="RefreshAsync"/>) are separate calls, so a page never waits for the service:
/// it reads what is here and schedules the revalidation in the background.
/// </summary>
public sealed class BackofficeDocumentsCache
{
    /// <summary>How long a stale copy is kept after its last use.</summary>
    public static readonly TimeSpan StaleRetention = TimeSpan.FromDays(7);

    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly BackofficeDocumentsClient _client;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;

    public BackofficeDocumentsCache(BackofficeDocumentsClient client, IMemoryCache cache, TimeProvider time, TimeSpan? ttl = null)
    {
        _client = client;
        _cache = cache;
        _time = time;
        Ttl = ttl is { } t && t > TimeSpan.Zero ? t : DefaultTtl;
    }

    /// <summary>How long an entry is served without asking the service again.</summary>
    public TimeSpan Ttl { get; }

    public static string CacheKey(string type, string locale) => $"p4:doc:{type}:{locale}";

    /// <summary>The cached entry, fresh or stale; null when there is none. Never calls the service.</summary>
    public CachedBackofficeDocument? Peek(string type, string locale) =>
        _cache.TryGetValue(CacheKey(type, locale), out CachedBackofficeDocument? entry) ? entry : null;

    public bool IsFresh(CachedBackofficeDocument entry) => _time.GetUtcNow() - entry.FetchedAt < Ttl;

    /// <summary>Drops the entry, e.g. when the service announced a new version.</summary>
    public void Remove(string type, string locale) => _cache.Remove(CacheKey(type, locale));

    /// <summary>
    /// Asks the service (conditionally when an entry exists) and stores the answer.
    /// Throws when the service fails; the stale entry, if any, stays in place.
    /// </summary>
    public async Task<BackofficeDocumentContent> RefreshAsync(string type, string locale, CancellationToken cancellationToken)
    {
        var entry = Peek(type, locale);
        var fetch = await _client.GetDocumentAsync(type, locale, entry?.ETag, cancellationToken);
        var fresh = fetch.NotModified && entry != null
            ? entry with { FetchedAt = _time.GetUtcNow() }
            : new CachedBackofficeDocument(fetch.Document!, fetch.ETag, _time.GetUtcNow());
        _cache.Set(CacheKey(type, locale), fresh, new MemoryCacheEntryOptions
        {
            SlidingExpiration = StaleRetention,
            Size = 1,
        });
        return fresh.Document;
    }
}
