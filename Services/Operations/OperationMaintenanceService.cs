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
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OperationMaintenanceService> _logger;

    public OperationMaintenanceService(IServiceProvider serviceProvider, ILogger<OperationMaintenanceService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var operations = scope.ServiceProvider.GetRequiredService<ApiOperationService>();
                await operations.SweepAsync(DateTimeOffset.UtcNow, stoppingToken);
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
