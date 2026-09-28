using System.Data.Common;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <summary>
/// Per-context list of outbox items waiting to be announced to <see cref="OutboxSignal"/>.
/// Filled by <see cref="BackofficeOutbox.Enqueue"/>, emptied by <see cref="OutboxSignalInterceptor"/>.
/// </summary>
internal sealed class OutboxPendingSignals
{
    private readonly object _gate = new();
    private readonly List<BackofficeOutboxItem> _enqueued = new();
    private readonly List<Guid> _saved = new();

    public OutboxSignal? Signal { get; set; }

    public void Add(BackofficeOutboxItem item)
    {
        lock (_gate) _enqueued.Add(item);
    }

    /// <summary>Items written by the last SaveChanges move from "enqueued" to "saved".</summary>
    public void MarkSaved(DbContext context)
    {
        lock (_gate)
        {
            for (var i = _enqueued.Count - 1; i >= 0; i--)
            {
                var state = context.Entry(_enqueued[i]).State;
                if (state is EntityState.Unchanged or EntityState.Modified)
                {
                    _saved.Add(_enqueued[i].Id);
                    _enqueued.RemoveAt(i);
                }
                else if (state is EntityState.Detached or EntityState.Deleted)
                {
                    _enqueued.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>The saved items are committed: announce them.</summary>
    public void Flush()
    {
        Guid[] ids;
        lock (_gate)
        {
            ids = _saved.ToArray();
            _saved.Clear();
        }
        var signal = Signal;
        if (signal == null) return;
        foreach (var id in ids) signal.Notify(id);
    }

    /// <summary>The transaction was rolled back: the saved items no longer exist.</summary>
    public void Discard()
    {
        lock (_gate) _saved.Clear();
    }

    public bool IsEmpty
    {
        get { lock (_gate) return _enqueued.Count == 0 && _saved.Count == 0; }
    }
}

/// <summary>
/// Announces enqueued outbox items only once they are committed: right after SaveChanges
/// when no explicit transaction is open, otherwise on commit. A rollback announces nothing.
///
/// Stateless (the state lives on the context), so one shared instance serves every context.
/// </summary>
internal sealed class OutboxSignalInterceptor : SaveChangesInterceptor, IDbTransactionInterceptor
{
    public static readonly OutboxSignalInterceptor Instance = new();

    private OutboxSignalInterceptor() { }

    private static OutboxPendingSignals? Pending(DbContext? context) =>
        context is ApplicationDbContext db && !db.OutboxPendingSignals.IsEmpty ? db.OutboxPendingSignals : null;

    private static void AfterSave(DbContext? context)
    {
        var pending = Pending(context);
        if (pending == null) return;
        pending.MarkSaved(context!);
        if (context!.Database.CurrentTransaction == null) pending.Flush();
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        AfterSave(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        AfterSave(eventData.Context);
        return ValueTask.FromResult(result);
    }

    // IDbTransactionInterceptor — only commit and rollback matter; everything else passes through.

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        Pending(eventData.Context)?.Flush();

    public Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Pending(eventData.Context)?.Flush();
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        Pending(eventData.Context)?.Discard();

    public Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Pending(eventData.Context)?.Discard();
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        Pending(eventData.Context)?.Discard();

    public Task TransactionFailedAsync(
        DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Pending(eventData.Context)?.Discard();
        return Task.CompletedTask;
    }
}
