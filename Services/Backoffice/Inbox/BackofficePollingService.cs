using System.Globalization;
using System.Net;
using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Backoffice.Inbox;

/// <summary>Outcome of one <see cref="BackofficePollingService.PollOnceAsync"/>.</summary>
/// <param name="Completed">False when the run stopped on an error; the cursor then stays at the last good page.</param>
public sealed record BackofficePollResult(int Pages, int Events, int Added, bool WindowClamped, bool Completed);

/// <summary>
/// Backup channel for webhooks (and the only one without a webhook secret): reads
/// <c>GET /v1/events?since=&lt;cursor&gt;</c> page by page and stores every event in the inbox,
/// where the webhook copy of the same event is deduplicated by id. The cursor
/// (<see cref="BackofficeStateKeys.EventsCursor"/>) is saved after every page, empty ones
/// included, so it survives a restart.
///
/// Registered only when a backoffice module is Shadow or On (never on a PrivateServer).
/// </summary>
public sealed class BackofficePollingService : BackgroundService
{
    public const string HttpClientName = "P4Backoffice.Events";
    public const int PageLimit = 100;

    /// <summary>Safety stop for one run; the rest follows on the next run.</summary>
    public const int MaxPagesPerRun = 50;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Where the very first run starts: recent enough to skip the history an import created,
    /// long enough to cover a deploy that took a while.
    /// </summary>
    public static readonly TimeSpan FirstRunLookback = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _httpClients;
    private readonly BackofficeInboxSignal _signal;
    private readonly IOptions<P4BackofficeProductOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<BackofficePollingService> _logger;

    public BackofficePollingService(
        IServiceScopeFactory scopes,
        IHttpClientFactory httpClients,
        BackofficeInboxSignal signal,
        IOptions<P4BackofficeProductOptions> options,
        TimeProvider time,
        ILogger<BackofficePollingService> logger)
    {
        _scopes = scopes;
        _httpClients = httpClients;
        _signal = signal;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public TimeSpan Interval
    {
        get
        {
            var o = _options.Value;
            return o.Polling.EffectiveInterval(o.Webhook.Configured);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never hold up application start
        var interval = Interval;
        _logger.LogInformation("Backoffice event polling every {Interval}", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backoffice event polling failed");
            }

            try
            {
                await Task.Delay(interval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Reads pages until <c>has_more</c> is false (at most <see cref="MaxPagesPerRun"/>).</summary>
    public async Task<BackofficePollResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var inbox = scope.ServiceProvider.GetRequiredService<BackofficeInbox>();
        var http = _httpClients.CreateClient(HttpClientName);

        var since = await BackofficeStateStore.GetAsync(db, BackofficeStateKeys.EventsCursor, cancellationToken);
        if (string.IsNullOrWhiteSpace(since))
        {
            since = (_time.GetUtcNow() - FirstRunLookback).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        var pages = 0;
        var events = 0;
        var added = 0;
        var clamped = false;
        var completed = true;
        try
        {
            while (pages < MaxPagesPerRun)
            {
                var page = await FetchAsync(http, since, cancellationToken);
                if (page == null)
                {
                    completed = false;
                    break;
                }
                pages++;

                foreach (var e in page.Data)
                {
                    events++;
                    if (!BackofficeInbox.IsStorable(e.Id, e.Type))
                    {
                        _logger.LogWarning("Backoffice event skipped: id or type missing or too long ({EventType})", e.Type);
                        continue;
                    }
                    if (await inbox.AddIfNewAsync(e.Id!, e.Type!, e.Data, BackofficeInboxSource.Poll, cancellationToken))
                    {
                        added++;
                    }
                }

                if (page.WindowClamped && !clamped)
                {
                    clamped = true;
                    _logger.LogError("Backoffice events: the stored cursor is older than the service keeps events; "
                                     + "events may have been missed, resynchronising from the source APIs");
                    if (!await ResyncAsync(scope.ServiceProvider, cancellationToken))
                    {
                        // Keep the old cursor: the next run is clamped again and retries the resync.
                        completed = false;
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(page.NextCursor))
                {
                    since = page.NextCursor;
                    await BackofficeStateStore.SetAsync(db, BackofficeStateKeys.EventsCursor, since, _time.GetUtcNow(), cancellationToken);
                }

                if (!page.HasMore) break;
            }
        }
        finally
        {
            if (added > 0) _signal.Notify();
        }

        if (added > 0)
        {
            _logger.LogInformation("Backoffice polling stored {Added} new event(s) of {Events}", added, events);
        }
        return new BackofficePollResult(pages, events, added, clamped, completed);
    }

    /// <summary>One page, or null after a logged failure.</summary>
    private async Task<BackofficeEventPage?> FetchAsync(HttpClient http, string since, CancellationToken cancellationToken)
    {
        var path = "v1/events?since=" + Uri.EscapeDataString(since)
                   + "&types=" + Uri.EscapeDataString(string.Join(",", BackofficeEventTypes.Subscribed))
                   + "&limit=" + PageLimit.ToString(CultureInfo.InvariantCulture);
        try
        {
            using var response = await http.GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    _logger.LogError("Backoffice events: {Status}; check P4Backoffice__SecretKey and its events:read scope", status);
                else
                    _logger.LogWarning("Backoffice events: HTTP {Status}; retrying on the next run", status);
                return null;
            }

            var page = await response.Content.ReadFromJsonAsync<BackofficeEventPage>(Json, cancellationToken);
            if (page == null)
            {
                _logger.LogWarning("Backoffice events: empty response; retrying on the next run");
            }
            return page;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Backoffice events unavailable; retrying on the next run");
            return null;
        }
    }

    /// <summary>Runs every <see cref="IBackofficeResync"/>. False when one of them failed.</summary>
    private async Task<bool> ResyncAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var resyncs = services.GetServices<IBackofficeResync>().ToList();
        if (resyncs.Count == 0)
        {
            _logger.LogWarning("Backoffice events: no module provides a resync yet; nothing to rebuild");
            return true;
        }

        var ok = true;
        foreach (var resync in resyncs)
        {
            try
            {
                await resync.ResyncAsync("window_clamped", cancellationToken);
                _logger.LogInformation("Backoffice resync {Name} finished", resync.Name);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                ok = false;
                _logger.LogError(ex, "Backoffice resync {Name} failed", resync.Name);
            }
        }
        return ok;
    }
}
