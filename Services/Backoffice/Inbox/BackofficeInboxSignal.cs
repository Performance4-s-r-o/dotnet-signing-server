using System.Threading.Channels;

namespace DotNetSigningServer.Services.Backoffice.Inbox;

/// <summary>
/// Wakes <see cref="BackofficeInboxProcessor"/> when new events were stored. Capacity 1 and
/// lossy on purpose: one pending wake-up covers any number of events, and a lost one only
/// means the next regular pass picks them up.
/// </summary>
public sealed class BackofficeInboxSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });

    /// <summary>Never blocks.</summary>
    public void Notify() => _channel.Writer.TryWrite(true);

    /// <summary>Waits for a notification or <paramref name="timeout"/>; true when notified.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_channel.Reader.TryRead(out _)) return true;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.WaitToReadAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false; // timed out: a regular pass
        }

        return _channel.Reader.TryRead(out _);
    }
}
