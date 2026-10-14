using DotNetSigningServer.Options;

namespace DotNetSigningServer.Services.Operations;

public static class OperationsRegistration
{
    /// <summary>
    /// Paid API operations: state, reservation, idempotent retries (#77, #82). The options are
    /// checked at startup — a heartbeat interval of zero would break the loop only after the
    /// work is done, one close to the lease would let live attempts expire, and a negative
    /// size would quietly stop keeping results.
    /// </summary>
    private static bool IsDeadline(int seconds) => seconds >= 1 && seconds <= OperationsOptions.MaxDeadlineSeconds;

    public static IServiceCollection AddApiOperations(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OperationsOptions>()
            .Bind(configuration.GetSection("Operations"))
            .Validate(
                o => o.HeartbeatInterval > TimeSpan.Zero
                     && o.HeartbeatInterval * 3 <= o.Lease
                     && o.ResultRetention > TimeSpan.Zero
                     && o.MaxStoredResultBytes >= 0
                     && IsDeadline(o.SignDeadlineSeconds)
                     && IsDeadline(o.DocumentDeadlineSeconds)
                     && IsDeadline(o.HeavyDeadlineSeconds)
                     && o.MaxAbandonedPerUser >= 1
                     && o.MaxAbandonedTotal >= o.MaxAbandonedPerUser,
                "Operations: HeartbeatInterval must be > 0 and at most a third of Lease, ResultRetention > 0, MaxStoredResultBytes >= 0, "
                + $"the three deadlines within 1..{OperationsOptions.MaxDeadlineSeconds} s (Cloudflare answers 524 after 100 s), MaxAbandonedPerUser >= 1 and MaxAbandonedTotal >= MaxAbandonedPerUser.")
            .ValidateOnStart();
        services.AddScoped<ApiOperationService>();
        services.AddHostedService<OperationMaintenanceService>();
        return services;
    }
}
