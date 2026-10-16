using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Backoffice;

/// <summary>
/// "Only one instance runs this job at a time" for the background jobs of the backoffice
/// integration (#79): a row <c>lease:&lt;name&gt;</c> in <see cref="BackofficeState"/> holding
/// the holder's token, its <c>UpdatedAt</c> the last renewal.
///
/// Replaces a session advisory lock, which needs one connection for the whole run and is not
/// reliable through a transaction pooler (Supabase <c>:6543</c>, pgbouncer): every step here is
/// a single autocommitted statement, so it works through any pooler. A lease not renewed for
/// its duration has expired and may be taken over — a crashed holder blocks the job at most
/// that long, and a single instance just takes its own lease every run.
///
/// The holder renews it in the background every third of the duration
/// (<see cref="Lost"/> is cancelled once that fails), and should call
/// <see cref="EnsureHeldAsync"/> right before a write that a newer holder must not overwrite.
/// Every database access happens in its own scope, so the heartbeat never shares the job's
/// <see cref="ApplicationDbContext"/>.
/// </summary>
public sealed class BackofficeLease : IAsyncDisposable
{
    public const string KeyPrefix = "lease:";

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lost = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _heartbeat;
    private DateTimeOffset _renewedAt;
    private int _disposed;

    private BackofficeLease(IServiceScopeFactory scopes, string key, string holder, TimeSpan duration, TimeProvider time, ILogger logger, DateTimeOffset renewedAt)
    {
        _scopes = scopes;
        Key = key;
        Holder = holder;
        Duration = duration;
        _time = time;
        _logger = logger;
        _renewedAt = renewedAt;
        _heartbeat = HeartbeatAsync();
    }

    public string Key { get; }

    public string Holder { get; }

    public TimeSpan Duration { get; }

    /// <summary>Cancelled once the lease can no longer be shown to be held; the job should stop.</summary>
    public CancellationToken Lost => _lost.Token;

    public static string KeyFor(string name) => KeyPrefix + name;

    /// <summary>Takes the lease <paramref name="name"/>, or null when another holder has it and it has not expired.</summary>
    public static async Task<BackofficeLease?> TryAcquireAsync(
        IServiceScopeFactory scopes, string name, TimeSpan duration, TimeProvider time, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        var key = KeyFor(name);
        var holder = Guid.NewGuid().ToString("N");
        var now = time.GetUtcNow();
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (!await TryTakeAsync(db, key, holder, now, duration, cancellationToken)) return null;
        }
        return new BackofficeLease(scopes, key, holder, duration, time, logger, now);
    }

    /// <summary>
    /// Whether a lease last renewed at <paramref name="renewedAt"/> by <paramref name="currentHolder"/>
    /// may be taken by <paramref name="holder"/> at <paramref name="now"/>: free, expired, or its own.
    /// </summary>
    public static bool CanTake(string? currentHolder, DateTimeOffset renewedAt, string holder, DateTimeOffset now, TimeSpan duration) =>
        currentHolder is null || currentHolder == holder || renewedAt + duration <= now;

    /// <summary>
    /// Renews the lease now. False when another holder has taken it (this one expired) — then
    /// <see cref="Lost"/> is cancelled. Throws when the database cannot be reached.
    /// </summary>
    public async Task<bool> RenewAsync(CancellationToken cancellationToken)
    {
        if (_lost.IsCancellationRequested) return false;
        var now = _time.GetUtcNow();
        bool held;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            held = await TryRenewAsync(db, Key, Holder, now, cancellationToken);
        }
        if (held)
        {
            _renewedAt = now;
            return true;
        }
        MarkLost("taken over by another instance");
        return false;
    }

    /// <summary>
    /// Renews before a write a newer holder must not overwrite (a cursor). False — and the job
    /// should stop without writing — when the lease is gone or cannot be renewed in time.
    /// </summary>
    public async Task<bool> EnsureHeldAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RenewAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Lease {Key}: could not renew", Key);
            if (_time.GetUtcNow() - _renewedAt >= Duration) MarkLost("not renewed in time");
            return false;
        }
    }

    private async Task HeartbeatAsync()
    {
        await Task.Yield();
        var every = Duration / 3;
        while (!_stop.IsCancellationRequested && !_lost.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(every, _time, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await EnsureHeldAsync(_stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private void MarkLost(string why)
    {
        if (_lost.IsCancellationRequested) return;
        _logger.LogWarning("Lease {Key} lost: {Why}", Key, why);
        try
        {
            _lost.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Stops the heartbeat and gives the lease back (when still held), so the next run need not wait for it to expire.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stop.Cancel();
        try
        {
            await _heartbeat;
        }
        catch (OperationCanceledException)
        {
        }

        if (!_lost.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await ReleaseAsync(db, Key, Holder, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // It expires on its own after Duration.
                _logger.LogWarning(ex, "Lease {Key}: could not release", Key);
            }
        }
        _stop.Dispose();
        _lost.Dispose();
    }

    // ---- storage: one statement each on PostgreSQL; read-modify-write elsewhere (InMemory) ----
    // Identifiers come from the EF model (SqlNames); every value is a parameter.

    internal static async Task<bool> TryTakeAsync(
        ApplicationDbContext db, string key, string holder, DateTimeOffset now, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
        {
            var t = SqlNames.Of<BackofficeState>(db);
            var k = t[nameof(BackofficeState.Key)];
            var v = t[nameof(BackofficeState.Value)];
            var u = t[nameof(BackofficeState.UpdatedAt)];
            // Inserted, or replaced when expired or already ours; 0 rows = someone else holds it.
            var upsert = $"INSERT INTO {t.Table} ({k}, {v}, {u}) VALUES ({{0}}, {{1}}, {{2}}) "
                         + $"ON CONFLICT ({k}) DO UPDATE SET {v} = EXCLUDED.{v}, {u} = EXCLUDED.{u} "
                         + $"WHERE {t.Table}.{u} <= {{3}} OR {t.Table}.{v} = EXCLUDED.{v}";
            var rows = await db.Database.ExecuteSqlRawAsync(
                upsert,
                [key, holder, now.ToUniversalTime(), (now - duration).ToUniversalTime()],
                cancellationToken);
            return rows == 1;
        }

        var row = await db.BackofficeStates.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row == null)
        {
            db.BackofficeStates.Add(new BackofficeState { Key = key, Value = holder, UpdatedAt = now });
        }
        else if (CanTake(row.Value, row.UpdatedAt, holder, now, duration))
        {
            row.Value = holder;
            row.UpdatedAt = now;
        }
        else
        {
            return false;
        }
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    internal static async Task<bool> TryRenewAsync(
        ApplicationDbContext db, string key, string holder, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
        {
            var t = SqlNames.Of<BackofficeState>(db);
            var renew = $"UPDATE {t.Table} SET {t[nameof(BackofficeState.UpdatedAt)]} = {{2}} "
                        + $"WHERE {t[nameof(BackofficeState.Key)]} = {{0}} AND {t[nameof(BackofficeState.Value)]} = {{1}}";
            var rows = await db.Database.ExecuteSqlRawAsync(
                renew,
                [key, holder, now.ToUniversalTime()],
                cancellationToken);
            return rows == 1;
        }

        var row = await db.BackofficeStates.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row == null || row.Value != holder) return false;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    internal static async Task ReleaseAsync(ApplicationDbContext db, string key, string holder, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
        {
            var t = SqlNames.Of<BackofficeState>(db);
            var delete = $"DELETE FROM {t.Table} WHERE {t[nameof(BackofficeState.Key)]} = {{0}} AND {t[nameof(BackofficeState.Value)]} = {{1}}";
            await db.Database.ExecuteSqlRawAsync(
                delete,
                [key, holder],
                cancellationToken);
            return;
        }

        var row = await db.BackofficeStates.FirstOrDefaultAsync(s => s.Key == key && s.Value == holder, cancellationToken);
        if (row == null) return;
        db.BackofficeStates.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
    }
}
