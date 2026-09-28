namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <summary>
/// Runs at the start of every dispatcher pass and delivers items by other means when the
/// service cannot be relied on (<see cref="BreakGlassEmailFallback"/>). Must never throw for a
/// single item; the dispatcher logs and carries on if it throws anyway.
/// </summary>
public interface IOutboxFallback
{
    /// <summary>Returns how many items it finished.</summary>
    Task<int> RunAsync(CancellationToken cancellationToken);
}
