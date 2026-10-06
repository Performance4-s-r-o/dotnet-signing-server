using DotNetSigningServer.Data;

namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <summary>
/// Sends the backoffice outbox in the background: woken by <see cref="OutboxSignal"/> right
/// after items are committed, otherwise every <see cref="PollInterval"/>. Also raises the
/// outbox alert every <see cref="MonitorInterval"/> and deletes old finished items daily.
///
/// Registered only when a backoffice module is Shadow or On (never on a PrivateServer), so a
/// service outage never touches request latency: requests only add rows.
/// </summary>
public sealed class BackofficeOutboxDispatcher : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MonitorInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CleanupInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// A pass stops claiming new batches after this, so a long backlog cannot hold off the
    /// periodic alert (the very case it is there to report). The drain resumes right away.
    /// </summary>
    public static readonly TimeSpan DrainBudget = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ErrorPause = TimeSpan.FromSeconds(30);

    private readonly OutboxProcessor _processor;
    private readonly OutboxSignal _signal;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<BackofficeOutboxDispatcher> _logger;
    private readonly IReadOnlyList<IOutboxFallback> _fallbacks;

    /// <summary>How long a pass waits for the signal; <see cref="PollInterval"/> unless a test sets it.</summary>
    private readonly TimeSpan _pollInterval;

    private DateTimeOffset _nextMonitor;
    private DateTimeOffset _nextCleanup;

    public BackofficeOutboxDispatcher(
        OutboxProcessor processor,
        OutboxSignal signal,
        IServiceScopeFactory scopes,
        TimeProvider time,
        ILogger<BackofficeOutboxDispatcher> logger,
        IEnumerable<IOutboxFallback>? fallbacks = null,
        TimeSpan? pollInterval = null)
    {
        _pollInterval = pollInterval ?? PollInterval;
        _fallbacks = fallbacks?.ToList() ?? [];
        _processor = processor;
        _signal = signal;
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never hold up application start
        var start = _time.GetUtcNow();
        _nextMonitor = start + MonitorInterval;
        _nextCleanup = start + MonitorInterval; // first cleanup shortly after start, then daily

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = _pollInterval;
            try
            {
                if (await RunPassAsync(stoppingToken))
                {
                    continue; // drain budget spent with more due: carry on without waiting
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backoffice outbox dispatcher pass failed");
                wait = ErrorPause;
            }

            try
            {
                await _signal.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One pass: the fallbacks (break-glass e-mail), then send what is due (batch after batch,
    /// for at most <see cref="DrainBudget"/>), then the periodic chores. Returns true when the budget ran out with more likely due.
    /// </summary>
    internal async Task<bool> RunPassAsync(CancellationToken cancellationToken)
    {
        foreach (var fallback in _fallbacks)
        {
            try
            {
                await fallback.RunAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Backoffice outbox fallback {Fallback} failed", fallback.GetType().Name);
            }
        }

        var drainStarted = _time.GetUtcNow();
        var moreDue = false;
        // A full batch means there may be more due right now.
        while (await _processor.DispatchDueAsync(cancellationToken) >= OutboxClaim.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_time.GetUtcNow() - drainStarted >= DrainBudget)
            {
                moreDue = true;
                break;
            }
        }

        var now = _time.GetUtcNow();
        if (now >= _nextMonitor)
        {
            _nextMonitor = now + MonitorInterval;
            await MonitorAsync(now, cancellationToken);
        }
        if (now >= _nextCleanup)
        {
            _nextCleanup = now + CleanupInterval;
            await CleanupAsync(now, cancellationToken);
        }
        return moreDue;
    }

    private async Task MonitorAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var snapshot = await OutboxHealth.ReadAsync(db, cancellationToken);
        foreach (var alert in OutboxHealth.Alerts(snapshot, now))
        {
            _logger.LogError("Backoffice outbox alert: {Alert}", alert);
        }
    }

    private async Task CleanupAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var deleted = await OutboxHealth.CleanupAsync(db, now, cancellationToken);
        if (deleted > 0)
        {
            _logger.LogInformation("Backoffice outbox cleanup deleted {Count} finished item(s)", deleted);
        }
    }
}
