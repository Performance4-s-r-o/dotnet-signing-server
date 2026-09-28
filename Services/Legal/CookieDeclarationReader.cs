using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Documents;

namespace DotNetSigningServer.Services.Legal;

/// <summary>One cookie of the declaration (a row of the table on the cookies page).</summary>
/// <param name="Category"><c>necessary</c>, <c>preferences</c>, <c>analytics</c> or <c>marketing</c>.</param>
/// <param name="Party"><c>first</c> or <c>third</c>.</param>
public sealed record CookieDeclarationCookie(
    string Name,
    string Provider,
    string Category,
    string Purpose,
    string Duration,
    string Party);

/// <summary>
/// The cookie declaration in force for one language, as served by
/// <c>GET /v1/cookie-declaration?locale=</c> and stored in
/// <c>BackofficeState["cookies:{locale}"]</c>.
/// </summary>
/// <param name="Locale">Language the snapshot is kept for (<c>en</c> or <c>cs</c>).</param>
/// <param name="Categories">Optional categories in use; empty = only necessary cookies (no banner).</param>
/// <param name="ETag">ETag of the response, for the conditional revalidation.</param>
public sealed record CookieDeclarationSnapshot(
    int Version,
    string Locale,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<string> Categories,
    IReadOnlyList<CookieDeclarationCookie> Cookies,
    string? ETag,
    DateTimeOffset FetchedAt)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>A stored snapshot; null when there is none or it is unreadable.</summary>
    public static CookieDeclarationSnapshot? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<CookieDeclarationSnapshot>(json, Json);
            return snapshot is { Version: > 0, Cookies: not null } ? snapshot with { Categories = snapshot.Categories ?? [] } : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a <c>CookieDeclaration</c> body. Cookies without a name are skipped; missing texts
    /// become empty. Throws <see cref="JsonException"/> on a body that is not that shape.
    /// </summary>
    public static CookieDeclarationSnapshot FromResponse(string locale, string body, string? etag, DateTimeOffset fetchedAt)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out var versionElement)
            || !versionElement.TryGetInt32(out var version)
            || version <= 0
            || !root.TryGetProperty("cookies", out var cookiesElement)
            || cookiesElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Cookie declaration response has no version or no cookies array");
        }

        var categories = new List<string>();
        if (root.TryGetProperty("categories", out var categoriesElement) && categoriesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in categoriesElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    categories.Add(item.GetString()!);
                }
            }
        }

        var cookies = new List<CookieDeclarationCookie>();
        foreach (var item in cookiesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = String(item, "name");
            if (string.IsNullOrEmpty(name)) continue;
            cookies.Add(new CookieDeclarationCookie(
                name,
                String(item, "provider"),
                String(item, "category"),
                String(item, "purpose"),
                String(item, "duration"),
                String(item, "party")));
        }

        DateTimeOffset? publishedAt = root.TryGetProperty("published_at", out var p)
                                      && p.ValueKind == JsonValueKind.String
                                      && DateTimeOffset.TryParse(p.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                                          System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

        return new CookieDeclarationSnapshot(version, locale, publishedAt, categories, cookies, etag, fetchedAt);
    }

    private static string String(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";
}

/// <summary>The cookie declaration for the cookies page. Never calls the service on the request.</summary>
public interface ICookieDeclarationSource
{
    /// <summary>The declaration for <paramref name="locale"/>; null when none is known. Never throws.</summary>
    Task<CookieDeclarationSnapshot?> GetAsync(string locale, CancellationToken cancellationToken = default);
}

/// <summary>Docs module Off or Shadow: no declaration; the cookies page shows its own table.</summary>
public sealed class NoCookieDeclarationSource : ICookieDeclarationSource
{
    public Task<CookieDeclarationSnapshot?> GetAsync(string locale, CancellationToken cancellationToken = default) =>
        Task.FromResult<CookieDeclarationSnapshot?>(null);
}

/// <summary>
/// Docs module On: the service's cookie declaration (<c>GET /v1/cookie-declaration?locale=</c>,
/// scope <c>docs:read</c>) for the table under the cookies policy.
///
/// A page reads the copy in memory; without one it reads the snapshot in
/// <c>BackofficeState["cookies:{locale}"]</c> (local database only). A copy older than the TTL
/// is revalidated in the background (<c>If-None-Match</c>), so the request never waits for the
/// service; after a failed call the service is left alone for <see cref="FailureBackoff"/>.
/// <c>cookie_declaration.published</c> (webhook or polling) and the <c>window_clamped</c>
/// resync call <see cref="RefreshAllAsync"/>, which refetches every language at once.
///
/// Vendored like <see cref="BackofficeDocumentsClient"/> (same named HttpClient and key).
/// TODO(P4.Backoffice.Sdk): once the package is restored in CI, call its generated
/// <c>V1.CookieDeclaration.GetAsync</c> in <see cref="FetchAsync"/>; callers depend only on
/// <see cref="CookieDeclarationSnapshot"/>.
/// </summary>
public sealed class CookieDeclarationReader : ICookieDeclarationSource
{
    public const string StateKeyPrefix = "cookies:";

    /// <summary>Upper bound of one background fetch (the HTTP timeout itself is 3 s).</summary>
    public static readonly TimeSpan BackgroundTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long background fetches are skipped after the service failed.</summary>
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClients;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<CookieDeclarationReader> _logger;
    private readonly ConcurrentDictionary<string, CookieDeclarationSnapshot> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _checkedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<CookieDeclarationSnapshot?>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private long _unavailableUntilTicks;

    public CookieDeclarationReader(
        IHttpClientFactory httpClients,
        IServiceScopeFactory scopes,
        TimeProvider time,
        ILogger<CookieDeclarationReader> logger,
        TimeSpan? ttl = null)
    {
        _httpClients = httpClients;
        _scopes = scopes;
        _time = time;
        _logger = logger;
        Ttl = ttl is { } t && t > TimeSpan.Zero ? t : BackofficeDocumentsCache.DefaultTtl;
    }

    /// <summary>How long a copy is served without asking the service again.</summary>
    public TimeSpan Ttl { get; }

    public static string StateKey(string locale) => StateKeyPrefix + LegalLocales.Normalize(locale);

    /// <summary>The background fetch started by the last <see cref="GetAsync"/>, if any (tests await it).</summary>
    internal Task<CookieDeclarationSnapshot?>? PendingRefresh { get; private set; }

    /// <summary>True while background fetches are paused after a failure.</summary>
    public bool InBackoff => _time.GetUtcNow().UtcTicks < Interlocked.Read(ref _unavailableUntilTicks);

    public async Task<CookieDeclarationSnapshot?> GetAsync(string locale, CancellationToken cancellationToken = default)
    {
        var key = LegalLocales.Normalize(locale);
        try
        {
            if (!_memory.TryGetValue(key, out var snapshot))
            {
                snapshot = await ReadStoredAsync(key, cancellationToken);
                if (snapshot != null) _memory.TryAdd(key, snapshot);
            }

            if (!_checkedAt.TryGetValue(key, out var checkedAt) || _time.GetUtcNow() - checkedAt >= Ttl)
            {
                StartRefresh(key);
            }
            return snapshot;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "[cookies] reading the cookie declaration ({Locale}) failed; the page shows its own table", key);
            return null;
        }
    }

    /// <summary>
    /// Fetches the declaration of every legal language, stores it and puts it in memory.
    /// <paramref name="force"/> skips the ETag (an event said there is a new version). Throws when
    /// the service or the database fails, so the caller (inbox, resync) retries.
    /// </summary>
    public async Task RefreshAllAsync(bool force, CancellationToken cancellationToken)
    {
        foreach (var locale in LegalLocales.Snapshot)
        {
            await RefreshAsync(locale, force, cancellationToken);
        }
    }

    /// <summary>
    /// Fetches (conditionally unless <paramref name="force"/>) and stores the declaration of one
    /// language. Null when the service has none published yet. Throws on failure.
    /// </summary>
    public async Task<CookieDeclarationSnapshot?> RefreshAsync(string locale, bool force, CancellationToken cancellationToken)
    {
        var key = LegalLocales.Normalize(locale);
        _memory.TryGetValue(key, out var current);
        try
        {
            var result = await FetchAsync(key, force ? null : current?.ETag, cancellationToken);
            Interlocked.Exchange(ref _unavailableUntilTicks, 0);
            var now = _time.GetUtcNow();
            switch (result)
            {
                case FetchResult.NotModified:
                    _checkedAt[key] = now;
                    return current;
                case FetchResult.NotFound:
                    // Nothing published yet: an answer, not an outage. The page keeps its own table.
                    _checkedAt[key] = now;
                    return current;
                case FetchResult.Fetched fetched:
                    var snapshot = fetched.Snapshot;
                    using (var scope = _scopes.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        await BackofficeStateStore.SetAsync(db, StateKey(key), snapshot.Serialize(), now, cancellationToken);
                    }
                    if (current?.Version != snapshot.Version)
                    {
                        _logger.LogInformation("[cookies] cookie declaration ({Locale}) is now version {Version} with {Count} cookies",
                            key, snapshot.Version, snapshot.Cookies.Count);
                    }
                    _memory[key] = snapshot;
                    _checkedAt[key] = now;
                    return snapshot;
                default:
                    return current;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _unavailableUntilTicks, (_time.GetUtcNow() + FailureBackoff).UtcTicks);
            throw;
        }
    }

    private void StartRefresh(string locale)
    {
        if (InBackoff) return;
        try
        {
            var lazy = _inFlight.GetOrAdd(locale, key => new Lazy<Task<CookieDeclarationSnapshot?>>(
                () => Task.Run(() => RunAsync(key))));
            PendingRefresh = lazy.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[cookies] starting the refresh of the cookie declaration ({Locale}) failed", locale);
        }
    }

    private async Task<CookieDeclarationSnapshot?> RunAsync(string locale)
    {
        try
        {
            using var timeout = new CancellationTokenSource(BackgroundTimeout);
            return await RefreshAsync(locale, force: false, timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[cookies] background refresh of the cookie declaration ({Locale}) failed; serving the stored copy", locale);
            return null;
        }
        finally
        {
            _inFlight.TryRemove(locale, out _);
        }
    }

    private async Task<CookieDeclarationSnapshot?> ReadStoredAsync(string locale, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = CookieDeclarationSnapshot.TryDeserialize(await BackofficeStateStore.GetAsync(db, StateKey(locale), cancellationToken));
        return stored is null ? null : stored with { Locale = locale };
    }

    private abstract record FetchResult
    {
        public sealed record NotModified : FetchResult;
        public sealed record NotFound : FetchResult;
        public sealed record Fetched(CookieDeclarationSnapshot Snapshot) : FetchResult;
    }

    private async Task<FetchResult> FetchAsync(string locale, string? etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/cookie-declaration?locale={Uri.EscapeDataString(locale)}");
        if (etag != null && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        using var response = await _httpClients.CreateClient(BackofficeDocumentsClient.HttpClientName).SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified && etag != null) return new FetchResult.NotModified();
        if (response.StatusCode == HttpStatusCode.NotFound) return new FetchResult.NotFound();
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return new FetchResult.Fetched(CookieDeclarationSnapshot.FromResponse(
                locale, body, response.Headers.ETag?.ToString(), _time.GetUtcNow()));
        }
        catch (JsonException ex)
        {
            // One exception type for "the service did not give us a usable answer".
            throw new HttpRequestException("Backoffice cookie declaration response is not a declaration", ex);
        }
    }
}
