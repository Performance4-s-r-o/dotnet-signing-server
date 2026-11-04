using DotNetSigningServer.Services.Ops;

namespace DotNetSigningServer.Services.Operations;

/// <summary>
/// Runs <see cref="ApiOperationService.SweepAsync"/> every minute: returns the credits of
/// attempts whose worker died (process restart mid-signature), purges stored results past
/// their retention and drops old rows.
///
/// Without it nothing is charged wrongly — a dead attempt delivered nothing — but its credits
/// stay held until a retry with the same key expires the lease itself.
/// </summary>
public class OperationMaintenanceService : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OperationMaintenanceService> _logger;
    private readonly IJobHeartbeat _heartbeat;

    public OperationMaintenanceService(IServiceProvider serviceProvider, ILogger<OperationMaintenanceService> logger, IJobHeartbeat? heartbeat = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _heartbeat = heartbeat ?? JobHeartbeat.Disabled;
        _heartbeat.Register(HeartbeatJobs.OperationMaintenance);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _heartbeat.RunAsync(HeartbeatJobs.OperationMaintenance, async ct =>
                {
                    using var scope = _serviceProvider.CreateScope();
                    var operations = scope.ServiceProvider.GetRequiredService<ApiOperationService>();
                    await operations.SweepAsync(DateTimeOffset.UtcNow, ct);
                }, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during API operation maintenance");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
