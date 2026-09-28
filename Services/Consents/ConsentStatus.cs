using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice.Documents;
using DotNetSigningServer.Services.Legal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Consents;

/// <summary>A document the user still has to confirm.</summary>
/// <param name="RequiredVersion">Oldest version that counts (<c>docs:meta.required_version</c>); null = any.</param>
/// <param name="RecordedVersion">Version of the user's newest record; null when there is none.</param>
public sealed record OutstandingConsent(string Document, string Action, int? RequiredVersion, int? RecordedVersion);

/// <summary>Consent state of one user, from local records only.</summary>
/// <param name="HasAnyRecord">False for users registered before consents were recorded.</param>
public sealed record ConsentStatus(IReadOnlyList<OutstandingConsent> Outstanding, bool HasAnyRecord)
{
    public bool IsCurrent => Outstanding.Count == 0;

    public static readonly ConsentStatus Current = new([], true);
}

/// <summary>The newest record of one document.</summary>
public sealed record LatestConsent(string Document, string Action, int Version, DateTimeOffset OccurredAt);

/// <summary>
/// Whether a user's newest records satisfy the requirements. Pure.
/// <para>
/// A <c>granted</c> requirement needs a newest record <c>granted</c>; an <c>acknowledged</c>
/// one accepts <c>granted</c> or <c>acknowledged</c>. The version must be at least
/// <c>required_version</c> of <c>docs:meta</c> (the newest material version in force); without
/// an entry there, any version counts. A newest record <c>revoked</c> never counts.
/// </para>
/// </summary>
public static class ConsentStatusEvaluator
{
    public static ConsentStatus Evaluate(
        IEnumerable<ConsentRequirement> requirements,
        DocumentsMeta? meta,
        IEnumerable<LatestConsent> latest)
    {
        var byDocument = latest
            .GroupBy(l => l.Document, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.MaxBy(l => l.OccurredAt)!, StringComparer.Ordinal);

        var outstanding = new List<OutstandingConsent>();
        foreach (var requirement in requirements)
        {
            var required = meta?.Documents.GetValueOrDefault(requirement.Document)?.RequiredVersion;
            byDocument.TryGetValue(requirement.Document, out var record);
            var actionOk = record?.Action switch
            {
                ConsentActions.Granted => true,
                ConsentActions.Acknowledged => !requirement.IsGrant,
                _ => false,
            };
            if (!actionOk || record!.Version < (required ?? 1))
            {
                outstanding.Add(new OutstandingConsent(requirement.Document, requirement.Action, required, record?.Version));
            }
        }
        return new ConsentStatus(outstanding, byDocument.Count > 0);
    }
}

/// <summary>Consent state of the signed-in user for the re-consent gate.</summary>
public interface IConsentStatusProvider
{
    /// <summary>Local records against <c>docs:meta</c>, cached for <see cref="ConsentStatusCache.Ttl"/>. Never calls the service.</summary>
    Task<ConsentStatus> GetAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>Something that reacts when a document changes in the service (Docs module events).</summary>
public interface IDocumentChangeListener
{
    /// <param name="requiresReconsent">The event said users have to consent again (<c>requires_reconsent</c>).</param>
    void OnDocumentChanged(string documentType, string eventType, bool requiresReconsent);
}

/// <summary>
/// In-memory cache of consent states (5 min per user) and of <c>docs:meta</c> for the notice
/// banner. <see cref="InvalidateAll"/> starts a new generation, so every user's state is
/// recomputed on the next page; called when a document is published with
/// <c>requires_reconsent</c>. Per instance: other instances catch up within the TTL.
/// </summary>
public sealed class ConsentStatusCache : IDocumentChangeListener
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly IMemoryCache _cache;
    private long _generation;

    public ConsentStatusCache(IMemoryCache cache)
    {
        _cache = cache;
    }

    public long Generation => Interlocked.Read(ref _generation);

    private string StatusKey(Guid userId) => $"consents:status:{Generation}:{userId:N}";

    private string MetaKey => $"consents:meta:{Generation}";

    public bool TryGet(Guid userId, out ConsentStatus? status) => _cache.TryGetValue(StatusKey(userId), out status);

    public void Set(Guid userId, ConsentStatus status) =>
        _cache.Set(StatusKey(userId), status, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl, Size = 1 });

    /// <summary>After the user confirmed: the next page reads the new records.</summary>
    public void Invalidate(Guid userId) => _cache.Remove(StatusKey(userId));

    public void InvalidateAll() => Interlocked.Increment(ref _generation);

    public async Task<DocumentsMeta?> GetMetaAsync(Func<Task<DocumentsMeta?>> load)
    {
        if (_cache.TryGetValue(MetaKey, out DocumentsMeta? meta)) return meta;
        meta = await load();
        _cache.Set(MetaKey, meta, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl, Size = 1 });
        return meta;
    }

    public void OnDocumentChanged(string documentType, string eventType, bool requiresReconsent)
    {
        if (requiresReconsent)
        {
            InvalidateAll();
        }
        else
        {
            // The upcoming banner may have changed; consent states have not.
            _cache.Remove(MetaKey);
        }
    }

    /// <summary>True once per user and TTL: Shadow mode logs a would-be redirect without flooding the log.</summary>
    public bool ShouldLogShadow(Guid userId)
    {
        var key = $"consents:shadow-logged:{userId:N}";
        if (_cache.TryGetValue(key, out _)) return false;
        _cache.Set(key, true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl, Size = 1 });
        return true;
    }
}

/// <inheritdoc />
public sealed class ConsentStatusProvider : IConsentStatusProvider
{
    /// <summary>Upper bound of records read per user (newest first); far above any real history.</summary>
    private const int MaxRecords = 500;

    private readonly ApplicationDbContext _db;
    private readonly ConsentStatusCache _cache;
    private readonly IOptions<P4BackofficeProductOptions> _options;

    public ConsentStatusProvider(ApplicationDbContext db, ConsentStatusCache cache, IOptions<P4BackofficeProductOptions> options)
    {
        _db = db;
        _cache = cache;
        _options = options;
    }

    public async Task<ConsentStatus> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGet(userId, out var cached) && cached != null) return cached;

        var meta = await _cache.GetMetaAsync(() => DocumentsMetaUpdater.ReadAsync(_db, cancellationToken));
        var status = ConsentStatusEvaluator.Evaluate(
            ConsentRequirements.From(_options.Value.Consents), meta, await LatestAsync(_db, userId, cancellationToken));
        _cache.Set(userId, status);
        return status;
    }

    /// <summary>The user's newest record per document.</summary>
    public static async Task<IReadOnlyList<LatestConsent>> LatestAsync(ApplicationDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.ConsentRecords.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.OccurredAt)
            .Take(MaxRecords)
            .Select(c => new LatestConsent(c.Document, c.Action, c.Version, c.OccurredAt))
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.Document, StringComparer.Ordinal).Select(g => g.First()).ToList();
    }
}

/// <summary>A document whose new version is announced for a future date (the notice banner).</summary>
public sealed record UpcomingDocumentNotice(string Document, string? Slug, int Version, DateTimeOffset EffectiveFrom);

/// <summary>
/// Announced versions of the consent documents for the "new terms apply from …" banner, from
/// the cached <c>docs:meta</c>. Consents module On only; never throws, never calls the service.
/// </summary>
public sealed class ConsentNoticeProvider
{
    private readonly ConsentStatusCache _cache;
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<P4BackofficeProductOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ConsentNoticeProvider> _logger;

    public ConsentNoticeProvider(
        ConsentStatusCache cache,
        IServiceScopeFactory scopes,
        IOptions<P4BackofficeProductOptions> options,
        TimeProvider time,
        ILogger<ConsentNoticeProvider> logger)
    {
        _cache = cache;
        _scopes = scopes;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<IReadOnlyList<UpcomingDocumentNotice>> GetUpcomingAsync()
    {
        var options = _options.Value;
        if (options.ModeFor(Backoffice.BackofficeModule.Consents) != Backoffice.BackofficeMode.On) return [];
        try
        {
            var meta = await _cache.GetMetaAsync(async () =>
            {
                using var scope = _scopes.CreateScope();
                return await DocumentsMetaUpdater.ReadAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
            });
            return Upcoming(ConsentRequirements.From(options.Consents), meta, _time.GetUtcNow());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[consents] upcoming document notice unavailable");
            return [];
        }
    }

    /// <summary>Announced versions of the required documents that take effect after <paramref name="now"/>. Pure.</summary>
    public static IReadOnlyList<UpcomingDocumentNotice> Upcoming(
        IEnumerable<ConsentRequirement> requirements, DocumentsMeta? meta, DateTimeOffset now) =>
        meta is null
            ? []
            : requirements
                .Select(r => (r.Document, Upcoming: meta.Documents.GetValueOrDefault(r.Document)?.Upcoming))
                .Where(x => x.Upcoming is { EffectiveFrom: { } from } && from > now)
                .Select(x => new UpcomingDocumentNotice(x.Document, LegalSlugMap.SlugFor(x.Document), x.Upcoming!.Version, x.Upcoming.EffectiveFrom!.Value))
                .OrderBy(n => n.EffectiveFrom)
                .ToList();
}
