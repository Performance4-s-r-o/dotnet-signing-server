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
    private static readonly TimeSpan ErrorPause = TimeSpan.FromSeconds(30);

    private readonly OutboxProcessor _processor;
    private readonly OutboxSignal _signal;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<BackofficeOutboxDispatcher> _logger;

    private DateTimeOffset _nextMonitor;
    private DateTimeOffset _nextCleanup;

    public BackofficeOutboxDispatcher(
        OutboxProcessor processor,
        OutboxSignal signal,
        IServiceScopeFactory scopes,
        TimeProvider time,
        ILogger<BackofficeOutboxDispatcher> logger)
    {
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
            var wait = PollInterval;
            try
            {
                await RunPassAsync(stoppingToken);
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

    /// <summary>One pass: send everything due (batch after batch), then the periodic chores.</summary>
    internal async Task RunPassAsync(CancellationToken cancellationToken)
    {
        // A full batch means there may be more due right now.
        while (await _processor.DispatchDueAsync(cancellationToken) >= OutboxClaim.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
