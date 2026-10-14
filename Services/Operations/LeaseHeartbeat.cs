namespace DotNetSigningServer.Services.Operations;

/// <summary>
/// Keeps the lease of a running attempt alive while its work runs (#77). Without it a keyed
/// operation that outlives the lease (it is not cancelled when the caller gives up) would be
/// taken for dead: the sweep would return its credits and a retry would start a second attempt
/// alongside it.
///
/// Each beat uses its own scope — the request's <c>DbContext</c> is busy with the work. The
/// loop stops when disposed (work finished) or when the attempt is no longer the running one.
/// </summary>
public sealed class LeaseHeartbeat : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public LeaseHeartbeat(IServiceScopeFactory scopes, Guid operationRowId, int attempt, TimeSpan interval, ILogger logger)
    {
        _loop = RunAsync(scopes, operationRowId, attempt, interval, logger, _stop.Token);
    }

    private static async Task RunAsync(
        IServiceScopeFactory scopes, Guid operationRowId, int attempt, TimeSpan interval, ILogger logger, CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stop))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var operations = scope.ServiceProvider.GetRequiredService<ApiOperationService>();
                    if (!await operations.ExtendLeaseAsync(operationRowId, attempt, stop)) return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One missed beat is fine; the lease is several beats long.
                    logger.LogWarning(ex, "Lease heartbeat failed for operation {OperationRowId}", operationRowId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Work finished.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _loop; } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
