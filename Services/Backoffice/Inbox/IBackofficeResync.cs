namespace DotNetSigningServer.Services.Backoffice.Inbox;

/// <summary>
/// Rebuilds a local snapshot straight from the service's source APIs (documents, price
/// list, …) when events may have been missed: <c>/v1/events</c> answered
/// <c>window_clamped</c> because the stored cursor was older than the 30-day window.
/// Every registered implementation runs; modules add theirs (documents, pricing).
/// Runs in the background.
/// </summary>
public interface IBackofficeResync
{
    string Name { get; }

    Task ResyncAsync(string reason, CancellationToken cancellationToken);
}
