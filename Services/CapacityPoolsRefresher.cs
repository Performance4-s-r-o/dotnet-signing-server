using DotNetSigningServer.Services.Settings;

namespace DotNetSigningServer.Services;

/// <summary>
/// Resizes pools G, H and A from the runtime settings while the server runs (#204). Env still
/// overrides (same names as before); empty or 0 = off. Shrinking never stops running operations.
/// </summary>
public sealed class CapacityPoolsRefresher : BackgroundService
{
    /// <summary>Half the settings poll interval: a change applies within one more poll.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly CapacityPools _pools;
    private readonly IRuntimeSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<CapacityPoolsRefresher> _logger;

    public CapacityPoolsRefresher(CapacityPools pools, IRuntimeSettings settings, TimeProvider time, ILogger<CapacityPoolsRefresher> logger)
    {
        _pools = pools;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    /// <summary>One pass; public for tests.</summary>
    public void Refresh()
    {
        var changed = _pools.ApplyRuntimeSettings(_settings);
        if (changed.Count > 0) _logger.LogInformation("Concurrency pools resized: {Changes}", string.Join(", ", changed));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Refresh();
            }
            catch (Exception ex)
            {
                // A bad value keeps the pools as they are; the next pass tries again.
                _logger.LogWarning(ex, "Concurrency pools were not resized");
            }
            try
            {
                await Task.Delay(Interval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
