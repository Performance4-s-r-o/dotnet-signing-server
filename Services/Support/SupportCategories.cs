using System.Collections.Concurrent;
using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Support;

/// <summary>A category of the support form. <see cref="Label"/> null = the form's own localized label.</summary>
public sealed record SupportCategoryOption(string Key, string? Label);

/// <summary>The service's categories in one language, as stored in <c>BackofficeState</c>.</summary>
public sealed record SupportCategoriesSnapshot(string Locale, IReadOnlyList<SupportCategoryOption> Categories, DateTimeOffset FetchedAt)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static SupportCategoriesSnapshot? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<SupportCategoriesSnapshot>(json, Json);
            return snapshot?.Categories is { Count: > 0 } ? snapshot : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a <c>GET /v1/support/categories</c> body (<c>SupportCategories</c>). Categories with
    /// an invalid key or no label are skipped. Throws <see cref="JsonException"/> on a body that is
    /// not that shape.
    /// </summary>
    public static SupportCategoriesSnapshot FromResponse(string locale, string body, DateTimeOffset fetchedAt)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Support categories response has no data array");
        }

        var categories = new List<SupportCategoryOption>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var key = item.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            var label = item.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()?.Trim() : null;
            if (key is null || string.IsNullOrEmpty(label)) continue;
            if (!SupportTicketRequest.IsCategoryKey(key)) continue;
            if (categories.Any(c => c.Key == key)) continue;
            categories.Add(new SupportCategoryOption(key, label));
        }
        return new SupportCategoriesSnapshot(locale, categories, fetchedAt);
    }
}

/// <summary>The service's categories per language, in memory. Empty until loaded or fetched.</summary>
public sealed class SupportCategoriesHolder
{
    private readonly ConcurrentDictionary<string, SupportCategoriesSnapshot> _byLocale = new(StringComparer.OrdinalIgnoreCase);

    public SupportCategoriesSnapshot? Get(string locale) => _byLocale.TryGetValue(locale, out var s) ? s : null;

    public void Set(SupportCategoriesSnapshot snapshot) => _byLocale[snapshot.Locale] = snapshot;
}

/// <summary>
/// Categories of the support form. <c>Modules:Support=On</c>: the service's list for the
/// language (kept up to date in the background by <see cref="SupportCategoriesWorker"/>), else
/// <see cref="SupportTicketRequest.DefaultCategories"/> with the form's own labels. Never calls
/// the service.
/// </summary>
public sealed class SupportCategoriesProvider
{
    private readonly SupportCategoriesHolder _holder;
    private readonly IOptions<P4BackofficeProductOptions> _options;

    public SupportCategoriesProvider(SupportCategoriesHolder holder, IOptions<P4BackofficeProductOptions> options)
    {
        _holder = holder;
        _options = options;
    }

    public static IReadOnlyList<SupportCategoryOption> Defaults { get; } =
        SupportTicketRequest.DefaultCategories.Select(k => new SupportCategoryOption(k, null)).ToList();

    public IReadOnlyList<SupportCategoryOption> For(string? locale)
    {
        if (_options.Value.ModeFor(BackofficeModule.Support) != BackofficeMode.On) return Defaults;
        var snapshot = _holder.Get(SupportCategoriesWorker.LocaleFor(locale));
        return snapshot?.Categories is { Count: > 0 } categories ? categories : Defaults;
    }
}

/// <summary>
/// Read-only client of <c>GET /v1/support/categories?locale=</c>. Vendored like
/// <c>BackofficePricingClient</c>; TODO(P4.Backoffice.Sdk): call
/// <c>V1.Support.Categories.GetAsync</c> here once the package is restored in CI.
/// Only <see cref="SupportCategoriesWorker"/> calls it.
/// </summary>
public sealed class SupportCategoriesClient
{
    public const string HttpClientName = "P4Backoffice.Support";

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly IHttpClientFactory _httpClients;

    public SupportCategoriesClient(IHttpClientFactory httpClients)
    {
        _httpClients = httpClients;
    }

    public async Task<string> GetAsync(string locale, CancellationToken cancellationToken)
    {
        using var response = await _httpClients.CreateClient(HttpClientName)
            .GetAsync($"v1/support/categories?locale={Uri.EscapeDataString(locale)}", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}

/// <summary>
/// Support Shadow/On: loads the stored categories (<c>BackofficeState["support:categories:{locale}"]</c>)
/// at startup (local database only), then fetches them for every site language right away and
/// every <see cref="Interval"/>, sooner after a failure. Shadow only logs how the service's keys
/// differ from the form's. Never blocks startup or a request on the service.
/// </summary>
public sealed class SupportCategoriesWorker : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    public const string StateKeyPrefix = "support:categories:";

    private readonly SupportCategoriesClient _client;
    private readonly SupportCategoriesHolder _holder;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<SupportCategoriesWorker> _logger;
    private readonly bool _shadow;

    public SupportCategoriesWorker(
        SupportCategoriesClient client,
        SupportCategoriesHolder holder,
        IServiceScopeFactory scopes,
        TimeProvider time,
        ILogger<SupportCategoriesWorker> logger,
        BackofficeMode mode)
    {
        _client = client;
        _holder = holder;
        _scopes = scopes;
        _time = time;
        _logger = logger;
        _shadow = mode == BackofficeMode.Shadow;
    }

    public static string StateKey(string locale) => StateKeyPrefix + locale;

    /// <summary>The site language the categories are kept for (<see cref="CultureUrls.Default"/> when unknown).</summary>
    public static string LocaleFor(string? culture)
    {
        var language = culture?.Split('-')[0].ToLowerInvariant();
        return CultureUrls.IsSupported(language) ? language! : CultureUrls.Default;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadStoredAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "[support] stored categories could not be loaded; the form's own list until the first refresh");
        }
        await base.StartAsync(cancellationToken);
    }

    /// <summary>Puts the stored snapshots into memory. Local database only.</summary>
    public async Task LoadStoredAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var locale in CultureUrls.Supported)
        {
            if (_holder.Get(locale) != null) continue;
            var stored = SupportCategoriesSnapshot.TryDeserialize(await BackofficeStateStore.GetAsync(db, StateKey(locale), cancellationToken));
            if (stored != null) _holder.Set(stored with { Locale = locale });
        }
    }

    /// <summary>Fetches, stores and publishes the categories of every site language. Throws on the first failure.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        foreach (var locale in CultureUrls.Supported)
        {
            var body = await _client.GetAsync(locale, cancellationToken);
            SupportCategoriesSnapshot snapshot;
            try
            {
                snapshot = SupportCategoriesSnapshot.FromResponse(locale, body, _time.GetUtcNow());
            }
            catch (JsonException ex)
            {
                throw new HttpRequestException("Backoffice support categories response is not a category list", ex);
            }
            if (snapshot.Categories.Count == 0)
            {
                _logger.LogWarning("[support] the service has no categories for {Locale}; keeping the previous list", locale);
                continue;
            }

            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await BackofficeStateStore.SetAsync(db, StateKey(locale), snapshot.Serialize(), snapshot.FetchedAt, cancellationToken);
            }
            _holder.Set(snapshot);

            if (_shadow) Compare(snapshot);
        }
    }

    private void Compare(SupportCategoriesSnapshot snapshot)
    {
        var service = snapshot.Categories.Select(c => c.Key).ToHashSet();
        var missing = SupportTicketRequest.DefaultCategories.Where(k => !service.Contains(k)).ToList();
        var extra = service.Where(k => !SupportTicketRequest.DefaultCategories.Contains(k)).ToList();
        if (missing.Count == 0 && extra.Count == 0)
        {
            _logger.LogInformation("[support] shadow: categories ({Locale}) match the form", snapshot.Locale);
        }
        else
        {
            _logger.LogWarning("[support] shadow: categories ({Locale}) differ from the form: missing {Missing}, extra {Extra}",
                snapshot.Locale, string.Join(", ", missing), string.Join(", ", extra));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = Interval;
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                delay = RetryAfterFailure;
                _logger.LogWarning(ex, "[support] category refresh failed; keeping the current list");
            }

            try
            {
                await Task.Delay(delay, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
