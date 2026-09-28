using System.Threading.Channels;

namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <summary>
/// Wakes the dispatcher when outbox items have been committed.
///
/// Bounded and lossy on purpose (DropOldest): a lost signal only means the item is picked
/// up by the next regular pass (<see cref="BackofficeOutboxDispatcher.PollInterval"/>).
/// </summary>
public sealed class OutboxSignal
{
    public const int Capacity = 1024;

    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });

    /// <summary>Announces a committed item. Never blocks.</summary>
    public void Notify(Guid itemId) => _channel.Writer.TryWrite(itemId);

    /// <summary>
    /// Waits until at least one item is announced or <paramref name="timeout"/> passes, then
    /// drains everything queued. Returns the announced ids (empty after a timeout).
    /// </summary>
    public async Task<IReadOnlyList<Guid>> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var ids = Drain();
        if (ids.Count > 0) return ids;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.WaitToReadAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out: a regular pass.
        }

        return Drain();
    }

    private List<Guid> Drain()
    {
        var ids = new List<Guid>();
        while (_channel.Reader.TryRead(out var id)) ids.Add(id);
        return ids;
    }
}
