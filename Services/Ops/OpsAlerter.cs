using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Ops;

/// <summary>Settings of the operational alert channel, section <c>OpsAlert</c>.</summary>
public sealed class OpsAlertOptions
{
    public const string SectionName = "OpsAlert";

    /// <summary>
    /// Incoming-webhook URL (Slack/Teams/Mattermost shape: <c>POST {"text": …}</c>). Empty = log only.
    /// The URL is the secret: it is never logged.
    /// </summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Minimum time between two alerts with the same key (default 6 h).</summary>
    public TimeSpan? Cooldown { get; set; }
}

/// <summary>
/// One output for alerts that must not depend on the system they report about (an outage of
/// the backoffice cannot be announced through the backoffice).
/// </summary>
public interface IOpsAlerter
{
    /// <summary>
    /// Sends <paramref name="text"/> unless the same <paramref name="key"/> was sent within the
    /// cooldown. Never throws and never blocks the caller. Put no personal data in the text.
    /// </summary>
    void Alert(string key, string text);
}

/// <summary>Posts alerts to <see cref="OpsAlertOptions.WebhookUrl"/>; with none it does nothing.</summary>
public sealed class OpsAlerter : IOpsAlerter
{
    public const string HttpClientName = "OpsAlert";
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromHours(6);
    internal const int MaxTextLength = 500;
    private const int MaxKeys = 200;

    private readonly IHttpClientFactory _httpClients;
    private readonly TimeProvider _time;
    private readonly ILogger<OpsAlerter> _logger;
    private readonly Uri? _url;
    private readonly TimeSpan _cooldown;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sent = new();

    public OpsAlerter(IHttpClientFactory httpClients, IOptions<OpsAlertOptions> options, TimeProvider time, ILogger<OpsAlerter> logger)
    {
        _httpClients = httpClients;
        _time = time;
        _logger = logger;
        _url = ParseUrl(options.Value.WebhookUrl);
        _cooldown = options.Value.Cooldown is { } c && c > TimeSpan.Zero ? c : DefaultCooldown;
    }

    public bool Enabled => _url != null;

    /// <summary>Null for an empty value; throws for one that is not an absolute https URL.</summary>
    public static Uri? ParseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "OpsAlert:WebhookUrl (environment variable OpsAlert__WebhookUrl) must be an absolute https URL or empty.");
        }
        return uri;
    }

    public void Alert(string key, string text)
    {
        if (_url == null) return;
        var now = _time.GetUtcNow();
        if (_sent.TryGetValue(key, out var last) && now - last < _cooldown) return;
        if (_sent.Count >= MaxKeys) Prune(now);
        _sent[key] = now;

        var body = text.Length > MaxTextLength ? text[..MaxTextLength] + "…" : text;
        _ = Task.Run(() => PostAsync(key, body));
    }

    internal async Task PostAsync(string key, string text)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await _httpClients.CreateClient(HttpClientName)
                .PostAsJsonAsync(_url, new { text = "[signing server] " + text }, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                // Allow a retry at the next occurrence instead of waiting out the cooldown.
                _sent.TryRemove(key, out _);
                _logger.LogWarning("Ops alert {Key}: webhook answered HTTP {Status}", key, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _sent.TryRemove(key, out _);
            // The exception text can carry the URL; log the type only.
            _logger.LogWarning("Ops alert {Key}: webhook not reached ({Error})", key, ex.GetType().Name);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (k, at) in _sent)
        {
            if (now - at >= _cooldown) _sent.TryRemove(k, out _);
        }
    }
}

public static class OpsAlertRegistration
{
    public static IServiceCollection AddOpsAlerts(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OpsAlertOptions>(configuration.GetSection(OpsAlertOptions.SectionName));
        // Fail at start on a malformed URL: a dead alert channel is not noticed until it is needed.
        OpsAlerter.ParseUrl(configuration[$"{OpsAlertOptions.SectionName}:WebhookUrl"]);
        services.AddHttpClient(OpsAlerter.HttpClientName).RemoveAllLoggers();
        services.AddSingleton<IOpsAlerter, OpsAlerter>();
        return services;
    }
}
