using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNetSigningServer.Services.Backoffice.Inbox;

namespace DotNetSigningServer.Services.Settings;

/// <summary>
/// Reads <c>GET /v1/settings</c> of the settings service into <see cref="RuntimeSettings"/>.
/// The product and its environment come from the key alone. Conditional (<c>If-None-Match</c>),
/// concurrent refreshes share one request, an error keeps what is in memory. Never on a request path.
/// </summary>
public sealed class RuntimeSettingsClient
{
    /// <summary>Named HttpClient: service root, bearer key, <see cref="RequestTimeout"/>.</summary>
    public const string HttpClientName = "P4Backoffice.Settings";

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex InstanceIdPattern = new("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.CultureInvariant);

    private readonly IHttpClientFactory _httpClients;
    private readonly RuntimeSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<RuntimeSettingsClient> _logger;
    private readonly object _gate = new();
    private Task? _inFlight;
    private long? _bootVersion;

    public RuntimeSettingsClient(IHttpClientFactory httpClients, RuntimeSettings settings, TimeProvider time, ILogger<RuntimeSettingsClient> logger)
    {
        _httpClients = httpClients;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    /// <summary>Stable id of this instance for the service's restart overview; null when the host name does not fit.</summary>
    public static string? InstanceId { get; } = InstanceIdPattern.IsMatch(Environment.MachineName) ? Environment.MachineName : null;

    /// <summary>Refreshes the settings; a refresh already running is joined, not repeated. Never throws (except on cancellation).</summary>
    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false } running) return running.WaitAsync(cancellationToken);
            _inFlight = FetchAsync();
            return _inFlight.WaitAsync(cancellationToken);
        }
    }

    private async Task FetchAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/settings");
            if (_settings.Version is { } known)
            {
                request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{known.ToString(CultureInfo.InvariantCulture)}\""));
            }
            if (InstanceId is { } instance) request.Headers.TryAddWithoutValidation("P4-Instance-Id", instance);
            if (_bootVersion is { } boot)
            {
                request.Headers.TryAddWithoutValidation("P4-Settings-Boot-Version", boot.ToString(CultureInfo.InvariantCulture));
            }

            using var response = await _httpClients.CreateClient(HttpClientName).SendAsync(request, CancellationToken.None);
            var now = _time.GetUtcNow();
            if (response.StatusCode == HttpStatusCode.NotModified && _settings.Version is not null)
            {
                _settings.MarkNotModified(now);
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[settings] GET /v1/settings answered {Status}; keeping the last known settings", (int)response.StatusCode);
                return;
            }

            var snapshot = RuntimeSettingsSnapshot.Parse(await response.Content.ReadAsStringAsync());
            var first = _settings.Version is null;
            _settings.Apply(snapshot, now);
            // The boot version is the first successful read and stays until the process restarts.
            _bootVersion ??= snapshot.Version;
            if (first)
            {
                _logger.LogInformation("[settings] runtime settings v{Version} loaded ({Count} items)", snapshot.Version, snapshot.Items.Count);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning("[settings] reading the runtime settings failed ({Error}); keeping the last known settings", ex.GetType().Name);
        }
    }
}

/// <summary>
/// Polls the settings every <c>poll_interval_seconds</c> (30 s by default). The first read happens at
/// start; a failure never stops the start. <c>settings.changed</c> refreshes sooner.
/// </summary>
public sealed class RuntimeSettingsWorker : BackgroundService
{
    /// <summary>Bounds of the poll interval the service may ask for.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(10);

    private readonly RuntimeSettingsClient _client;
    private readonly RuntimeSettings _settings;
    private readonly TimeProvider _time;

    public RuntimeSettingsWorker(RuntimeSettingsClient client, RuntimeSettings settings, TimeProvider time)
    {
        _client = client;
        _settings = settings;
        _time = time;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _client.RefreshAsync(stoppingToken);
                await Task.Delay(Interval(_settings.Snapshot), _time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public static TimeSpan Interval(RuntimeSettingsSnapshot? snapshot)
    {
        var seconds = snapshot?.PollIntervalSeconds ?? RuntimeSettingsSnapshot.DefaultPollIntervalSeconds;
        var interval = TimeSpan.FromSeconds(seconds);
        return interval < MinInterval ? MinInterval : interval > MaxInterval ? MaxInterval : interval;
    }
}

/// <summary><c>settings.changed</c>: refresh now (the event carries keys and a version, never values).</summary>
public sealed class SettingsChangedHandler : IBackofficeEventHandler
{
    private readonly RuntimeSettingsClient _client;

    public SettingsChangedHandler(RuntimeSettingsClient client)
    {
        _client = client;
    }

    public IReadOnlyCollection<string> Types { get; } = [BackofficeEventTypes.SettingsChanged];

    public Task HandleAsync(BackofficeEvent evt, CancellationToken cancellationToken) => _client.RefreshAsync(cancellationToken);
}

/// <summary>Logs the ignored env overrides once and checks them at start (an unreadable one stops it).</summary>
internal sealed class RuntimeSettingsStartupReport(
    RuntimeSettingsOverrides overrides,
    bool serviceConfigured,
    ILogger<RuntimeSettingsStartupReport> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var ignored in overrides.Ignored)
        {
            logger.LogWarning("[settings] {Override}", ignored);
        }
        if (!serviceConfigured)
        {
            logger.LogInformation("[settings] no settings service: defaults in code and env overrides apply, kill switches stay closed");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
