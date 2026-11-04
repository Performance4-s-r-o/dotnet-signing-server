using System.Text;

namespace DotNetSigningServer.Services.Ops;

/// <summary>A periodic job (#167).</summary>
/// <param name="Name">Stable job name; the check name too when the job is in no group.</param>
/// <param name="Interval">How often the job runs at most apart; a group's cycle must leave room for it.</param>
public sealed record HeartbeatJob(string Name, TimeSpan Interval);

/// <summary>One Healthchecks check shared by several jobs.</summary>
/// <param name="Cycle">Length of a cycle, aligned to UTC (multiples since 1970-01-01).</param>
public sealed record HeartbeatGroup(string Name, TimeSpan Cycle, IReadOnlyList<HeartbeatJob> Jobs);

/// <summary>
/// Every periodic job and the one map job → check (#167). The free Healthchecks tier is shared
/// with the portal and the backoffice, so the jobs share four checks.
/// </summary>
public static class HeartbeatJobs
{
    public static readonly HeartbeatJob PresignCleanup = new("presign-cleanup", PresignCleanupService.CleanupInterval);
    public static readonly HeartbeatJob OperationMaintenance = new("operation-maintenance", Operations.OperationMaintenanceService.Interval);
    public static readonly HeartbeatJob DataRetention = new("data-retention", DataRetentionService.Interval);
    public static readonly HeartbeatJob BackofficeOutbox = new("backoffice-outbox", Backoffice.Outbox.BackofficeOutboxDispatcher.PollInterval);
    public static readonly HeartbeatJob BackofficeInbox = new("backoffice-inbox", Backoffice.Inbox.BackofficeInboxProcessor.PollInterval);
    public static readonly HeartbeatJob BackofficePolling = new("backoffice-polling", DotNetSigningServer.Options.P4BackofficeProductOptions.PollingOptions.MaxInterval);
    public static readonly HeartbeatJob ConsentReconciliation = new("consent-reconcile", Consents.ConsentReconciliationService.CheckEvery);
    public static readonly HeartbeatJob PricingRefresh = new("pricing-refresh", Pricing.PricingSnapshotWorker.Interval);
    public static readonly HeartbeatJob PricingUpcoming = new("pricing-upcoming", Pricing.PricingUpcomingCheck.Interval);
    public static readonly HeartbeatJob SupportCategories = new("support-categories", Support.SupportCategoriesWorker.Interval);

    public static IReadOnlyList<HeartbeatJob> All { get; } =
    [
        PresignCleanup, OperationMaintenance, DataRetention, BackofficeOutbox, BackofficeInbox,
        BackofficePolling, ConsentReconciliation, PricingRefresh, PricingUpcoming, SupportCategories,
    ];

    /// <summary>The checks. A job left out gets a check of its own name, never none.</summary>
    public static IReadOnlyList<HeartbeatGroup> Groups { get; } =
    [
        new("dotnet-maintenance", TimeSpan.FromMinutes(30), [PresignCleanup, OperationMaintenance]),
        new("dotnet-backoffice-sync", TimeSpan.FromMinutes(30), [BackofficeOutbox, BackofficeInbox, BackofficePolling]),
        // Hourly jobs drift by their own run time, so a 1 h cycle would now and then see none: 2 h.
        new("dotnet-backoffice-hourly", TimeSpan.FromHours(2), [ConsentReconciliation, PricingRefresh, SupportCategories]),
        new("dotnet-daily", TimeSpan.FromDays(1), [DataRetention, PricingUpcoming]),
    ];

    /// <summary>The check a job pings: its group, otherwise its own name.</summary>
    public static string CheckNameFor(HeartbeatJob job) =>
        Groups.FirstOrDefault(g => g.Jobs.Any(j => j.Name == job.Name))?.Name ?? job.Name;

    /// <summary>Problems of the map (empty = fine): a job in two groups, a group named like a job, an empty group.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<HeartbeatGroup> groups, IReadOnlyList<HeartbeatJob> jobs)
    {
        var problems = new List<string>();
        var seen = new Dictionary<string, string>();
        foreach (var group in groups)
        {
            if (jobs.Any(j => j.Name == group.Name)) problems.Add($"group {group.Name} has the name of a job");
            if (group.Jobs.Count == 0) problems.Add($"group {group.Name} is empty");
            if (group.Cycle <= TimeSpan.Zero) problems.Add($"group {group.Name} has no cycle");
            foreach (var job in group.Jobs)
            {
                if (seen.TryGetValue(job.Name, out var other)) problems.Add($"job {job.Name} is in {other} and {group.Name}");
                seen[job.Name] = group.Name;
            }
        }
        return problems;
    }
}

/// <summary>
/// Pings a dead man's switch around the periodic jobs (#167), the Healthchecks URL shape
/// <c>&lt;base&gt;/&lt;check&gt;/start</c>, <c>&lt;base&gt;/&lt;check&gt;</c>, <c>&lt;base&gt;/&lt;check&gt;/fail</c>.
/// The same rules as the portal's worker (<c>scripts/cron-heartbeat.js</c>) for a group:
/// <list type="bullet">
/// <item>a run belongs to the cycle it began in; <c>/start</c> at the first run of a cycle;</item>
/// <item>success only once every job of the group registered in this process succeeded in the cycle;</item>
/// <item>a failure of any job is <c>/fail</c> at once with the job's name and the exception type (once per job and cycle);</item>
/// <item>a cycle that ended without that is <c>/fail</c> (<c>incomplete, missing: …</c>) when the next one begins;</item>
/// <item>a cycle that began before this process is not judged: no <c>/start</c>, no incomplete.</item>
/// </list>
/// State is in memory only. The job never waits for a ping and never fails because of one.
/// </summary>
public interface IJobHeartbeat
{
    /// <summary>The job runs in this process; its group waits for it. Call from the constructor.</summary>
    void Register(HeartbeatJob job);

    /// <summary>Runs <paramref name="work"/> between the pings; its exception is rethrown after the fail ping.</summary>
    Task<T> RunAsync<T>(HeartbeatJob job, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);

    /// <summary>A job without a result.</summary>
    Task RunAsync(HeartbeatJob job, Func<CancellationToken, Task> work, CancellationToken cancellationToken);
}

/// <summary>Sends the pings to <c>HEARTBEAT_BASE_URL</c>; with none it only runs the job.</summary>
public sealed class JobHeartbeat : IJobHeartbeat
{
    public const string HttpClientName = "Heartbeat";
    public const string ConfigKey = "HEARTBEAT_BASE_URL";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    internal const int MaxBodyLength = 500;

    /// <summary>Pings of one check waiting behind a slow service; more are dropped.</summary>
    internal const int MaxPendingPerCheck = 5;

    /// <summary>No base URL: no ping, the job just runs.</summary>
    public static readonly IJobHeartbeat Disabled = new JobHeartbeat(null, null!, TimeProvider.System, null);

    private readonly string? _base;
    private readonly IHttpClientFactory _httpClients;
    private readonly TimeProvider _time;
    private readonly ILogger<JobHeartbeat>? _logger;
    private readonly DateTimeOffset _processStart;
    private readonly object _gate = new();
    private readonly Dictionary<string, GroupState> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GroupState> _groupOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Sender> _senders = new(StringComparer.Ordinal);

    private sealed class GroupState(HeartbeatGroup group)
    {
        public HeartbeatGroup Group { get; } = group;
        public HashSet<string> Registered { get; } = new(StringComparer.Ordinal);
        public Cycle? Current;
    }

    private sealed class Cycle(DateTimeOffset start, bool full)
    {
        public DateTimeOffset Start { get; } = start;
        public bool Full { get; } = full;
        public HashSet<string> Done { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FailedJobs { get; } = new(StringComparer.Ordinal);
        public bool Started;
        public bool Succeeded;
    }

    private sealed class Sender
    {
        public Task Chain = Task.CompletedTask;
        public int Pending;
    }

    public JobHeartbeat(Uri? baseUrl, IHttpClientFactory httpClients, TimeProvider time, ILogger<JobHeartbeat>? logger,
        IReadOnlyList<HeartbeatGroup>? groups = null)
    {
        _base = baseUrl?.ToString().TrimEnd('/');
        _httpClients = httpClients;
        _time = time;
        _logger = logger;
        _processStart = time.GetUtcNow();
        foreach (var group in groups ?? HeartbeatJobs.Groups)
        {
            var state = new GroupState(group);
            _groups[group.Name] = state;
            foreach (var job in group.Jobs) _groupOf[job.Name] = state;
        }
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

    public void Register(HeartbeatJob job)
    {
        lock (_gate)
        {
            if (_groupOf.TryGetValue(job.Name, out var group)) group.Registered.Add(job.Name);
        }
    }

    public Task RunAsync(HeartbeatJob job, Func<CancellationToken, Task> work, CancellationToken cancellationToken) =>
        RunAsync<bool>(job, async ct =>
        {
            await work(ct);
            return true;
        }, cancellationToken);

    public async Task<T> RunAsync<T>(HeartbeatJob job, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        if (_base == null)
        {
            return await work(cancellationToken);
        }

        var cycle = Begin(job);
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
            Finish(job, cycle, Describe(ex));
            throw;
        }
        Finish(job, cycle, null);
        return result;
    }

    /// <summary>Waits until every ping queued so far is sent or dropped (tests).</summary>
    internal Task DrainAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll(_senders.Values.Select(s => s.Chain));
        }
    }

    /// <summary>Exception types only: a message can carry data of a user.</summary>
    internal static string Describe(Exception ex)
    {
        var text = new StringBuilder(ex.GetType().Name);
        if (ex.InnerException is { } inner) text.Append(" <- ").Append(inner.GetType().Name);
        return text.ToString();
    }

    private static DateTimeOffset CycleStartOf(HeartbeatGroup group, DateTimeOffset at) =>
        DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds() / (long)group.Cycle.TotalMilliseconds * (long)group.Cycle.TotalMilliseconds);

    /// <summary>A run begins; returns the start of its group's cycle (null for a job in no group).</summary>
    private DateTimeOffset? Begin(HeartbeatJob job)
    {
        lock (_gate)
        {
            if (!_groupOf.TryGetValue(job.Name, out var group))
            {
                Enqueue(job.Name, "/start", job.Name);
                return null;
            }

            group.Registered.Add(job.Name); // a job that runs is registered, whatever its constructor did
            var start = CycleStartOf(group.Group, _time.GetUtcNow());
            Advance(group, start);
            var cycle = group.Current!;
            if (cycle.Start == start && cycle.Full && !cycle.Started)
            {
                cycle.Started = true;
                Enqueue(group.Group.Name, "/start", $"first: {job.Name}");
            }
            return start;
        }
    }

    /// <summary>Moves the group to the cycle at <paramref name="start"/>, judging the one that ends.</summary>
    private void Advance(GroupState group, DateTimeOffset start)
    {
        var current = group.Current;
        if (current != null && start <= current.Start) return;
        if (current is { Full: true, Succeeded: false } && current.FailedJobs.Count == 0)
        {
            var missing = group.Registered.Where(j => !current.Done.Contains(j)).Order(StringComparer.Ordinal);
            Enqueue(group.Group.Name, "/fail", $"cycle {current.Start:yyyy-MM-ddTHH:mm:ssZ} incomplete, missing: {string.Join(", ", missing)}");
        }
        else if (current is { Full: false, Succeeded: false } && current.FailedJobs.Count == 0)
        {
            _logger?.LogInformation("Heartbeat {Check}: cycle {Start:u} began before this process, not judged", group.Group.Name, current.Start);
        }
        group.Current = new Cycle(start, start >= _processStart);
    }

    /// <summary>A run ended: <paramref name="failure"/> is the exception type, null on success.</summary>
    private void Finish(HeartbeatJob job, DateTimeOffset? cycleStart, string? failure)
    {
        lock (_gate)
        {
            if (cycleStart is not { } started || !_groupOf.TryGetValue(job.Name, out var group))
            {
                Enqueue(job.Name, failure is null ? "" : "/fail", failure is null ? job.Name : $"{job.Name}: {failure}");
                return;
            }

            var cycle = group.Current;
            var current = cycle != null && cycle.Start == started;
            if (failure != null)
            {
                // Reported for a closed cycle too; once per job and cycle.
                if (current && !cycle!.FailedJobs.Add(job.Name)) return;
                Enqueue(group.Group.Name, "/fail", $"{job.Name}: {failure}");
                return;
            }
            if (!current) return; // finished for a closed cycle
            cycle!.Done.Add(job.Name);
            if (cycle.FailedJobs.Count == 0 && !cycle.Succeeded && group.Registered.All(cycle.Done.Contains))
            {
                cycle.Succeeded = true;
                Enqueue(group.Group.Name, "", "all ok: " + string.Join(", ", group.Registered.Order(StringComparer.Ordinal)));
            }
        }
    }

    /// <summary>Queues a ping behind the earlier ones of the same check, so they arrive in order. Under the lock.</summary>
    private void Enqueue(string check, string suffix, string body)
    {
        if (!_senders.TryGetValue(check, out var sender)) _senders[check] = sender = new Sender();
        if (sender.Pending >= MaxPendingPerCheck)
        {
            _logger?.LogWarning("Heartbeat {Check}{Suffix} dropped: earlier pings are still pending", check, suffix);
            return;
        }
        sender.Pending++;
        sender.Chain = sender.Chain.ContinueWith(
            _ => SendAsync(check, suffix, body, sender),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();
    }

    private async Task SendAsync(string check, string suffix, string body, Sender sender)
    {
        try
        {
            using var cts = new CancellationTokenSource(RequestTimeout);
            var text = body.Length > MaxBodyLength ? body[..MaxBodyLength] : body;
            using var content = new StringContent(text, Encoding.UTF8, "text/plain");
            using var response = await _httpClients.CreateClient(HttpClientName)
                .PostAsync($"{_base}/{Uri.EscapeDataString(check)}{suffix}", content, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Heartbeat {Check}{Suffix}: HTTP {Status}", check, suffix, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // The exception text can carry the URL (it holds the ping key); log the type only.
            _logger?.LogWarning("Heartbeat {Check}{Suffix} not delivered ({Error})", check, suffix, ex.GetType().Name);
        }
        finally
        {
            lock (_gate)
            {
                sender.Pending--;
            }
        }
    }
}

public static class JobHeartbeatRegistration
{
    public static IServiceCollection AddJobHeartbeats(this IServiceCollection services, IConfiguration configuration)
    {
        // Fail at start on a malformed URL or map: a dead switch is noticed only when it is needed.
        var url = JobHeartbeat.ParseUrl(configuration[JobHeartbeat.ConfigKey]);
        var problems = HeartbeatJobs.Validate(HeartbeatJobs.Groups, HeartbeatJobs.All);
        if (problems.Count > 0) throw new InvalidOperationException("Heartbeat groups: " + string.Join("; ", problems));
        services.AddHttpClient(JobHeartbeat.HttpClientName, c => c.Timeout = JobHeartbeat.RequestTimeout).RemoveAllLoggers();
        services.AddSingleton<IJobHeartbeat>(sp => url is null
            ? JobHeartbeat.Disabled
            : new JobHeartbeat(url, sp.GetRequiredService<IHttpClientFactory>(), sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetRequiredService<ILogger<JobHeartbeat>>()));
        return services;
    }
}
