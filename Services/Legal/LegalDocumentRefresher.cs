using System.Collections.Concurrent;
using System.Net;
using DotNetSigningServer.Services.Backoffice.Documents;

namespace DotNetSigningServer.Services.Legal;

/// <summary>
/// Fetches a document from the service into <see cref="BackofficeDocumentsCache"/> and, when
/// asked, stores it in the <c>LegalDocuments</c> snapshot.
///
/// Pages use <see cref="RefreshInBackground"/>: it returns at once, runs at most one fetch
/// per document at a time, and after a failure leaves the service alone for
/// <see cref="FailureBackoff"/> — so an outage never costs a page the 3 s timeout, let alone
/// repeatedly. Event handlers and resyncs await <see cref="RefreshAsync"/> and let a failure
/// propagate so they are retried.
/// </summary>
public sealed class LegalDocumentRefresher
{
    /// <summary>Upper bound of one background fetch (the HTTP timeout itself is 3 s).</summary>
    public static readonly TimeSpan BackgroundTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long background fetches are skipped after the service failed.</summary>
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

    private readonly BackofficeDocumentsCache _cache;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<LegalDocumentRefresher> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<BackofficeDocumentContent?>>> _inFlight = new();
    private readonly ConcurrentDictionary<string, (int Version, string Hash)> _saved = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _notFoundUntil = new();
    private long _unavailableUntilTicks;

    public LegalDocumentRefresher(
        BackofficeDocumentsCache cache,
        IServiceScopeFactory scopes,
        TimeProvider time,
        ILogger<LegalDocumentRefresher> logger)
    {
        _cache = cache;
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    /// <summary>True while background fetches are paused after a failure.</summary>
    public bool InBackoff => _time.GetUtcNow().UtcTicks < Interlocked.Read(ref _unavailableUntilTicks);

    /// <summary>
    /// Fetches (conditionally) and, with <paramref name="saveSnapshot"/>, upserts the snapshot
    /// row. Throws when the service or the database fails.
    /// </summary>
    public async Task<BackofficeDocumentContent> RefreshAsync(string type, string locale, bool saveSnapshot, CancellationToken cancellationToken)
    {
        BackofficeDocumentContent document;
        try
        {
            document = await _cache.RefreshAsync(type, locale, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Not published in the service: an answer, not an outage. Not asked again for a TTL.
            _notFoundUntil[$"{type}:{locale}"] = _time.GetUtcNow() + _cache.Ttl;
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _unavailableUntilTicks, (_time.GetUtcNow() + FailureBackoff).UtcTicks);
            throw;
        }
        Interlocked.Exchange(ref _unavailableUntilTicks, 0);
        _notFoundUntil.TryRemove($"{type}:{locale}", out _);

        if (saveSnapshot)
        {
            await SaveSnapshotAsync(document, cancellationToken);
        }
        return document;
    }

    /// <summary>
    /// Starts a fetch outside the request and returns its task (null result on failure, on
    /// backoff, or when the service is unreachable). Never throws.
    /// </summary>
    public Task<BackofficeDocumentContent?> RefreshInBackground(string type, string locale, bool saveSnapshot)
    {
        if (InBackoff
            || _notFoundUntil.TryGetValue($"{type}:{locale}", out var notFoundUntil) && _time.GetUtcNow() < notFoundUntil)
        {
            return Task.FromResult<BackofficeDocumentContent?>(null);
        }

        var key = $"{type}:{locale}:{(saveSnapshot ? "save" : "read")}";
        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<BackofficeDocumentContent?>>(
            () => Task.Run(() => RunAsync(key, type, locale, saveSnapshot))));
        return lazy.Value;
    }

    private async Task<BackofficeDocumentContent?> RunAsync(string key, string type, string locale, bool saveSnapshot)
    {
        try
        {
            using var timeout = new CancellationTokenSource(BackgroundTimeout);
            return await RefreshAsync(type, locale, saveSnapshot, timeout.Token);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("[legal-docs] {Type}/{Locale} is not published in the service; serving the local text", type, locale);
            return null;
        }
        catch (Exception ex)
        {
            // No document text or user data in the message: type and locale only.
            _logger.LogWarning(ex, "[legal-docs] background refresh of {Type}/{Locale} failed; serving the cached copy or snapshot", type, locale);
            return null;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private async Task SaveSnapshotAsync(BackofficeDocumentContent document, CancellationToken cancellationToken)
    {
        var key = $"{document.Type}:{document.Locale}";
        if (_saved.TryGetValue(key, out var last) && last.Version == document.Version && last.Hash == document.ContentHash)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<LegalDocumentsSnapshotWriter>();
        if (await writer.UpsertAsync(document, cancellationToken))
        {
            _logger.LogInformation("[legal-docs] snapshot of {Type}/{Locale} updated to version {Version}",
                document.Type, document.Locale, document.Version);
        }
        _saved[key] = (document.Version, document.ContentHash);
    }
}
