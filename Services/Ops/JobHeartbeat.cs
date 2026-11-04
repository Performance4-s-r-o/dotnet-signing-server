using System.Text;

namespace DotNetSigningServer.Services.Ops;

/// <summary>
/// A periodic job as Healthchecks knows it (#167).
/// </summary>
/// <param name="Name">Stable check slug, the last segment of the ping URL.</param>
/// <param name="MinPingInterval">Fewer pings for a loop that runs every few seconds: start and
/// success go out at most this often; a failure and the first success after it always go out.</param>
public sealed record HeartbeatJob(string Name, TimeSpan MinPingInterval)
{
    public HeartbeatJob(string name) : this(name, TimeSpan.Zero) { }
}

/// <summary>Every check the server pings; the names are what the Healthchecks checks are created with.</summary>
public static class HeartbeatJobs
{
    public static readonly HeartbeatJob PresignCleanup = new("dotnet-presign-cleanup");
    public static readonly HeartbeatJob OperationMaintenance = new("dotnet-operation-maintenance");
    public static readonly HeartbeatJob DataRetention = new("dotnet-data-retention");
    public static readonly HeartbeatJob BackofficeOutbox = new("dotnet-backoffice-outbox", TimeSpan.FromMinutes(1));
    public static readonly HeartbeatJob BackofficeInbox = new("dotnet-backoffice-inbox", TimeSpan.FromMinutes(1));
    public static readonly HeartbeatJob BackofficePolling = new("dotnet-backoffice-polling");
    public static readonly HeartbeatJob ConsentReconciliation = new("dotnet-consent-reconcile");
    public static readonly HeartbeatJob PricingRefresh = new("dotnet-pricing-refresh");
    public static readonly HeartbeatJob PricingUpcoming = new("dotnet-pricing-upcoming");
    public static readonly HeartbeatJob SupportCategories = new("dotnet-support-categories");

    public static IReadOnlyList<HeartbeatJob> All { get; } =
    [
        PresignCleanup, OperationMaintenance, DataRetention, BackofficeOutbox, BackofficeInbox,
        BackofficePolling, ConsentReconciliation, PricingRefresh, PricingUpcoming, SupportCategories,
    ];
}

/// <summary>
/// Pings a dead man's switch around each run of a periodic job (#167): <c>&lt;base&gt;/&lt;job&gt;/start</c>,
/// <c>&lt;base&gt;/&lt;job&gt;</c> on success and <c>&lt;base&gt;/&lt;job&gt;/fail</c> on an exception, the
/// Healthchecks URL shape. The job never waits for a ping and never fails because of one.
/// </summary>
public interface IJobHeartbeat
{
    /// <summary>Runs <paramref name="work"/> between the pings; its exception is rethrown after the fail ping.</summary>
    /// <param name="summarize">A short summary of the result for the success ping; no personal data.</param>
    Task<T> RunAsync<T>(HeartbeatJob job, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken, Func<T, string?>? summarize = null);

    /// <summary>A job without a result.</summary>
    Task RunAsync(HeartbeatJob job, Func<CancellationToken, Task> work, CancellationToken cancellationToken);
}

/// <summary>Sends the pings to <c>HEARTBEAT_BASE_URL</c>; with none it only runs the job.</summary>
public sealed class JobHeartbeat : IJobHeartbeat
{
    public const string HttpClientName = "Heartbeat";
    public const string ConfigKey = "HEARTBEAT_BASE_URL";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    internal const int MaxBodyLength = 200;

    /// <summary>Pings of one job waiting behind a slow service; more are dropped.</summary>
    internal const int MaxPendingPerJob = 5;

    /// <summary>No base URL: no ping, the job just runs.</summary>
    public static readonly IJobHeartbeat Disabled = new JobHeartbeat(null, null!, TimeProvider.System, null);

    private readonly string? _base;
    private readonly IHttpClientFactory _httpClients;
    private readonly TimeProvider _time;
    private readonly ILogger<JobHeartbeat>? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, JobState> _jobs = new(StringComparer.Ordinal);

    private sealed class JobState
    {
        public DateTimeOffset? LastReported;
        public bool Failed;
        public DateTimeOffset? LastFail;
        public Task Chain = Task.CompletedTask;
        public int Pending;
    }

    public JobHeartbeat(Uri? baseUrl, IHttpClientFactory httpClients, TimeProvider time, ILogger<JobHeartbeat>? logger)
    {
        _base = baseUrl?.ToString().TrimEnd('/');
        _httpClients = httpClients;
        _time = time;
        _logger = logger;
    }

    public bool Enabled => _base != null;

    /// <summary>Null for an empty or unresolved value; throws for one that is not an absolute http(s) URL.</summary>
    public static Uri? ParseUrl(string? value)
    {
        if (DotNetSigningServer.Options.EnvValues.IsUnset(value)) return null;
        if (!Uri.TryCreate(value!.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException($"{ConfigKey} must be an absolute http(s) URL without a query, or empty.");
        }
        return uri;
    }

    public Task RunAsync(HeartbeatJob job, Func<CancellationToken, Task> work, CancellationToken cancellationToken) =>
        RunAsync<bool>(job, async ct =>
        {
            await work(ct);
            return true;
        }, cancellationToken);

    public async Task<T> RunAsync<T>(HeartbeatJob job, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken, Func<T, string?>? summarize = null)
    {
        if (_base == null)
        {
            return await work(cancellationToken);
        }

        var report = BeginRun(job);
        if (report) Enqueue(job, "/start", null);

        T result;
        try
        {
            result = await work(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // shutdown, not a failure
        }
        catch (Exception ex)
        {
            if (RecordFailure(job)) Enqueue(job, "/fail", Describe(ex));
            throw;
        }

        if (RecordSuccess(job, report))
        {
            string? summary = null;
            try
            {
                summary = summarize?.Invoke(result);
            }
            catch (Exception)
            {
                // A summary is a nicety; the success ping goes out without it.
            }
            Enqueue(job, "", summary);
        }
        return result;
    }

    /// <summary>Waits until every ping queued so far is sent or dropped (tests).</summary>
    internal Task DrainAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll(_jobs.Values.Select(s => s.Chain));
        }
    }

    /// <summary>Exception types only: a message can carry data of a user.</summary>
    internal static string Describe(Exception ex)
    {
        var text = new StringBuilder(ex.GetType().Name);
        if (ex.InnerException is { } inner) text.Append(" <- ").Append(inner.GetType().Name);
        return text.ToString();
    }

    private JobState State(HeartbeatJob job)
    {
        if (!_jobs.TryGetValue(job.Name, out var state))
        {
            _jobs[job.Name] = state = new JobState();
        }
        return state;
    }

    private bool BeginRun(HeartbeatJob job)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var state = State(job);
            var due = state.LastReported is not { } last || now - last >= job.MinPingInterval;
            if (due) state.LastReported = now;
            return due;
        }
    }

    private bool RecordFailure(HeartbeatJob job)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var state = State(job);
            var send = !state.Failed || state.LastFail is not { } last || now - last >= job.MinPingInterval;
            state.Failed = true;
            if (send) state.LastFail = now;
            return send;
        }
    }

    private bool RecordSuccess(HeartbeatJob job, bool reported)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var state = State(job);
            var send = reported || state.Failed;
            if (state.Failed) state.LastReported = now;
            state.Failed = false;
            return send;
        }
    }

    /// <summary>Queues a ping behind the earlier ones of the same job, so they arrive in order.</summary>
    private void Enqueue(HeartbeatJob job, string suffix, string? body)
    {
        lock (_gate)
        {
            var state = State(job);
            if (state.Pending >= MaxPendingPerJob)
            {
                _logger?.LogWarning("Heartbeat {Job}{Suffix} dropped: earlier pings are still pending", job.Name, suffix);
                return;
            }
            state.Pending++;
            state.Chain = state.Chain.ContinueWith(
                _ => SendAsync(job, suffix, body, state),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private async Task SendAsync(HeartbeatJob job, string suffix, string? body, JobState state)
    {
        try
        {
            using var cts = new CancellationTokenSource(RequestTimeout);
            var text = body is null ? "" : body.Length > MaxBodyLength ? body[..MaxBodyLength] : body;
            using var content = new StringContent(text, Encoding.UTF8, "text/plain");
            using var response = await _httpClients.CreateClient(HttpClientName)
                .PostAsync($"{_base}/{Uri.EscapeDataString(job.Name)}{suffix}", content, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Heartbeat {Job}{Suffix}: HTTP {Status}", job.Name, suffix, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // The exception text can carry the URL (it holds the ping key); log the type only.
            _logger?.LogWarning("Heartbeat {Job}{Suffix} not delivered ({Error})", job.Name, suffix, ex.GetType().Name);
        }
        finally
        {
            lock (_gate)
            {
                state.Pending--;
            }
        }
    }
}

public static class JobHeartbeatRegistration
{
    public static IServiceCollection AddJobHeartbeats(this IServiceCollection services, IConfiguration configuration)
    {
        // Fail at start on a malformed URL: a dead switch is noticed only when it is needed.
        var url = JobHeartbeat.ParseUrl(configuration[JobHeartbeat.ConfigKey]);
        services.AddHttpClient(JobHeartbeat.HttpClientName, c => c.Timeout = JobHeartbeat.RequestTimeout).RemoveAllLoggers();
        services.AddSingleton<IJobHeartbeat>(sp => url is null
            ? JobHeartbeat.Disabled
            : new JobHeartbeat(url, sp.GetRequiredService<IHttpClientFactory>(), sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetRequiredService<ILogger<JobHeartbeat>>()));
        return services;
    }
}
