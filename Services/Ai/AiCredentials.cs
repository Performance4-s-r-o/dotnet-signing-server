using System.Globalization;
using System.Text.Json;
using DotNetSigningServer.Exceptions;

namespace DotNetSigningServer.Services.Ai;

/// <summary>An AI call refused or failed with a code of its own; maps to an HTTP status in the controller.</summary>
public sealed class AiException(string code, int statusCode, string? detail = null) : ApiValidationException(code, detail ?? code)
{
    /// <summary>AI switched off (runtime setting <c>ai_enabled</c>).</summary>
    public const string Disabled = "AI_DISABLED";

    /// <summary>No credentials, no usable provider, or every provider of the call failed over.</summary>
    public const string Unavailable = "AI_UNAVAILABLE";

    /// <summary>The provider declined the request (refusal / safety); not retried with another provider.</summary>
    public const string Refused = "AI_REFUSED";

    /// <summary>The provider answered, but not in the requested shape.</summary>
    public const string InvalidOutput = "AI_INVALID_OUTPUT";

    /// <summary>The provider refused the request itself (4xx other than exhaustion); not retried.</summary>
    public const string ProviderError = "AI_PROVIDER_ERROR";

    public int StatusCode { get; } = statusCode;
}

/// <summary>A credential of one provider. The value is a secret: never logged, never stored.</summary>
public sealed class AiCredential(string kind, string value)
{
    public string Kind { get; } = kind;

    public string Value { get; } = value;

    public override string ToString() => $"{Kind}:***";
}

/// <summary>One provider from <c>POST /v1/ai/credentials</c>, in the service's order.</summary>
public sealed record AiProvider(string Id, string Provider, string Model, string Region, AiCredential Credential);

/// <summary>A successful <c>POST /v1/ai/credentials</c>.</summary>
public sealed record AiCredentialSet(
    bool Enabled,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> AllowedRegions,
    int MaxProvidersPerCall,
    IReadOnlyList<AiProvider> Providers)
{
    /// <summary>Parses a response body; throws <see cref="JsonException"/> when it is not one.</summary>
    public static AiCredentialSet Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !root.TryGetProperty("expires_at", out var expires) || expires.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(expires.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiresAt))
        {
            throw new JsonException("Credentials response has no enabled or expires_at");
        }

        var regions = new List<string>();
        var maxProviders = 1;
        if (root.TryGetProperty("policy", out var policy) && policy.ValueKind == JsonValueKind.Object)
        {
            if (policy.TryGetProperty("allowed_regions", out var allowed) && allowed.ValueKind == JsonValueKind.Array)
            {
                regions.AddRange(allowed.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!));
            }
            if (policy.TryGetProperty("max_providers_per_call", out var max) && max.ValueKind == JsonValueKind.Number && max.TryGetInt32(out var n) && n > 0) maxProviders = n;
        }

        var providers = new List<AiProvider>();
        if (root.TryGetProperty("providers", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in list.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var credential = p.TryGetProperty("credential", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
                var kind = String(credential, "kind");
                var value = String(credential, "value");
                var id = String(p, "id");
                if (id is null || kind is null || value is null) continue;
                providers.Add(new AiProvider(id, String(p, "provider") ?? "", String(p, "model") ?? "", String(p, "region") ?? "",
                    new AiCredential(kind, value)));
            }
        }

        return new AiCredentialSet(enabled.ValueKind == JsonValueKind.True, expiresAt, regions, maxProviders, providers);
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

/// <summary>Where AI credentials come from. Never env or configuration.</summary>
public interface IAiCredentialsSource
{
    /// <summary>The credentials in force; null when there are none (no service, never fetched, expired).</summary>
    Task<AiCredentialSet?> GetAsync(CancellationToken cancellationToken);

    /// <summary>A provider this instance reported into cooldown; skipped until the next refresh.</summary>
    void MarkCoolingDown(string providerId);

    bool IsCoolingDown(string providerId);
}

/// <summary>No AI service (private server, no address): there are never credentials.</summary>
public sealed class NoAiCredentials : IAiCredentialsSource
{
    public Task<AiCredentialSet?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<AiCredentialSet?>(null);

    public void MarkCoolingDown(string providerId)
    {
    }

    public bool IsCoolingDown(string providerId) => false;
}

/// <summary>
/// <c>POST /v1/ai/credentials</c> (scope <c>ai:credentials</c>): kept in memory until <c>expires_at</c>
/// and refreshed ahead of it. A failed refresh keeps the copy until it expires; then AI is refused.
/// An answer with <c>enabled: false</c> replaces the copy at once (never extended).
/// </summary>
public sealed class AiCredentialsClient : IAiCredentialsSource
{
    public const string HttpClientName = "P4Backoffice.Ai";

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long before <c>expires_at</c> a refresh starts in the background.</summary>
    public static readonly TimeSpan RefreshAhead = TimeSpan.FromSeconds(120);

    private readonly IHttpClientFactory _httpClients;
    private readonly TimeProvider _time;
    private readonly ILogger<AiCredentialsClient> _logger;
    private readonly object _gate = new();
    private readonly HashSet<string> _coolingDown = new(StringComparer.Ordinal);
    private volatile AiCredentialSet? _current;
    private Task? _inFlight;

    public AiCredentialsClient(IHttpClientFactory httpClients, TimeProvider time, ILogger<AiCredentialsClient> logger)
    {
        _httpClients = httpClients;
        _time = time;
        _logger = logger;
    }

    public async Task<AiCredentialSet?> GetAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var current = _current;
        if (current != null && now < current.ExpiresAt)
        {
            if (current.ExpiresAt - now <= RefreshAhead) _ = RefreshAsync();
            return current;
        }

        await RefreshAsync().WaitAsync(cancellationToken);
        current = _current;
        return current != null && _time.GetUtcNow() < current.ExpiresAt ? current : null;
    }

    public void MarkCoolingDown(string providerId)
    {
        lock (_gate) _coolingDown.Add(providerId);
    }

    public bool IsCoolingDown(string providerId)
    {
        lock (_gate) return _coolingDown.Contains(providerId);
    }

    /// <summary>One refresh at a time; callers join the running one. Never throws.</summary>
    public Task RefreshAsync()
    {
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false } running) return running;
            _inFlight = FetchAsync();
            return _inFlight;
        }
    }

    private async Task FetchAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/ai/credentials")
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };
            if (Settings.RuntimeSettingsClient.InstanceId is { } instance) request.Headers.TryAddWithoutValidation("P4-Instance-Id", instance);
            using var response = await _httpClients.CreateClient(HttpClientName).SendAsync(request, CancellationToken.None);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[ai] POST /v1/ai/credentials answered {Status}; keeping the current copy until it expires", (int)response.StatusCode);
                return;
            }
            var set = AiCredentialSet.Parse(await response.Content.ReadAsStringAsync());
            lock (_gate)
            {
                _current = set;
                _coolingDown.Clear();
            }
            // Ids, providers and regions only; never a credential value.
            _logger.LogInformation("[ai] credentials refreshed: enabled={Enabled}, {Count} providers ({Providers}), until {ExpiresAt:O}",
                set.Enabled, set.Providers.Count, string.Join(", ", set.Providers.Select(p => $"{p.Provider}/{p.Region}/{p.Id}")), set.ExpiresAt);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning("[ai] reading the AI credentials failed ({Error}); keeping the current copy until it expires", ex.GetType().Name);
        }
    }
}
