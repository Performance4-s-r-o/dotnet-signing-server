using Microsoft.Extensions.Caching.Memory;

namespace DotNetSigningServer.Services.Backoffice.Consents;

/// <summary>Prompts of one context and locale, with the ETag they were served with.</summary>
public sealed record CachedConsentPrompts(IReadOnlyList<BackofficeConsentPrompt> Prompts, string? ETag, DateTimeOffset FetchedAt);

/// <summary>
/// In-memory copy of the consent wording, keyed <c>p4:prompt:{context}:{locale}</c>.
/// Fresh for <see cref="Ttl"/>, revalidated with <c>If-None-Match</c> after that, kept for
/// seven days (sliding) as the copy served while the service is unreachable.
///
/// Reading (<see cref="Peek"/>) and revalidating (<see cref="RefreshAsync"/>) are separate on
/// purpose, like the documents cache: a sign-up page shows what is here and never waits for
/// the service. A page with no copy at all cannot show a checkbox honestly, so the caller
/// decides what to do — it is the one that knows whether the consent is required.
/// </summary>
public sealed class ConsentPromptsCache
{
    /// <summary>How long a stale copy is kept after its last use.</summary>
    public static readonly TimeSpan StaleRetention = TimeSpan.FromDays(7);

    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly BackofficeConsentPromptsClient _client;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;

    public ConsentPromptsCache(BackofficeConsentPromptsClient client, IMemoryCache cache, TimeProvider time, TimeSpan? ttl = null)
    {
        _client = client;
        _cache = cache;
        _time = time;
        Ttl = ttl is { } t && t > TimeSpan.Zero ? t : DefaultTtl;
    }

    /// <summary>How long an entry is served without asking the service again.</summary>
    public TimeSpan Ttl { get; }

    public static string CacheKey(string context, string locale) => $"p4:prompt:{context}:{locale}";

    /// <summary>The cached entry, fresh or stale; null when there is none. Never calls the service.</summary>
    public CachedConsentPrompts? Peek(string context, string locale) =>
        _cache.TryGetValue(CacheKey(context, locale), out CachedConsentPrompts? entry) ? entry : null;

    public bool IsFresh(CachedConsentPrompts entry) => _time.GetUtcNow() - entry.FetchedAt < Ttl;

    /// <summary>Drops the entry, e.g. when the service announced a new published version.</summary>
    public void Remove(string context, string locale) => _cache.Remove(CacheKey(context, locale));

    /// <summary>
    /// Asks the service (conditionally when an entry exists) and stores the answer.
    /// Throws when the service fails; the stale entry, if any, stays in place.
    /// </summary>
    public async Task<IReadOnlyList<BackofficeConsentPrompt>> RefreshAsync(string context, string locale, CancellationToken cancellationToken)
    {
        var entry = Peek(context, locale);
        var fetch = await _client.GetPromptsAsync(locale, context, entry?.ETag, cancellationToken);
        var fresh = fetch.NotModified && entry != null
            ? entry with { FetchedAt = _time.GetUtcNow() }
            : new CachedConsentPrompts(fetch.Prompts ?? [], fetch.ETag, _time.GetUtcNow());

        _cache.Set(CacheKey(context, locale), fresh, new MemoryCacheEntryOptions { SlidingExpiration = StaleRetention });
        return fresh.Prompts;
    }
}
