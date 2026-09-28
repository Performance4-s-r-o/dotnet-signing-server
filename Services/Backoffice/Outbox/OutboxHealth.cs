using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <summary>State of the outbox for <c>/Admin</c> and the dispatcher's periodic alert.</summary>
public sealed record OutboxHealthSnapshot(
    IReadOnlyDictionary<string, int> Counts,
    DateTimeOffset? OldestPendingAt,
    string? LastError,
    DateTimeOffset? LastErrorItemCreatedAt)
{
    public int Count(string status) => Counts.TryGetValue(status, out var n) ? n : 0;
}

/// <summary>Outbox metrics, alert rules and admin actions.</summary>
public static class OutboxHealth
{
    /// <summary>More pending items than this raises an alert.</summary>
    public const int PendingAlertThreshold = 100;

    /// <summary>A pending item older than this raises an alert.</summary>
    public static readonly TimeSpan OldestPendingAlertAge = TimeSpan.FromHours(1);

    public static async Task<OutboxHealthSnapshot> ReadAsync(ApplicationDbContext db, CancellationToken cancellationToken = default)
    {
        var counts = await db.BackofficeOutboxItems.AsNoTracking()
            .GroupBy(i => i.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);

        var oldestPending = await db.BackofficeOutboxItems.AsNoTracking()
            .Where(i => i.Status == BackofficeOutboxStatus.Pending)
            .OrderBy(i => i.CreatedAt)
            .Select(i => (DateTimeOffset?)i.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        // "Last" by creation — the table has no updated-at column, and this is only a hint.
        var lastError = await db.BackofficeOutboxItems.AsNoTracking()
            .Where(i => i.LastError != null)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new { i.LastError, i.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        return new OutboxHealthSnapshot(counts, oldestPending, lastError?.LastError, lastError?.CreatedAt);
    }

    /// <summary>Alert messages for the snapshot; empty when healthy.</summary>
    public static IReadOnlyList<string> Alerts(OutboxHealthSnapshot snapshot, DateTimeOffset now)
    {
        var alerts = new List<string>();
        var pending = snapshot.Count(BackofficeOutboxStatus.Pending);
        if (pending > PendingAlertThreshold)
        {
            alerts.Add($"{pending} backoffice outbox items are pending (threshold {PendingAlertThreshold})");
        }
        if (snapshot.OldestPendingAt is { } oldest && now - oldest > OldestPendingAlertAge)
        {
            alerts.Add($"Oldest pending backoffice outbox item is from {oldest:o} (older than {OldestPendingAlertAge.TotalMinutes:0} min)");
        }
        return alerts;
    }

    /// <summary>
    /// Puts every Blocked item back to Pending with a fresh retry round (after the API key has
    /// been fixed). Returns the requeued ids.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> RequeueBlockedAsync(
        ApplicationDbContext db, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var blocked = await db.BackofficeOutboxItems
            .Where(i => i.Status == BackofficeOutboxStatus.Blocked)
            .ToListAsync(cancellationToken);
        foreach (var item in blocked)
        {
            item.Status = BackofficeOutboxStatus.Pending;
            item.Attempts = 0;
            item.NextAttemptAt = now;
            item.LockedUntil = null;
        }
        await db.SaveChangesAsync(cancellationToken);
        return blocked.Select(i => i.Id).ToList();
    }

    /// <summary>Items that no longer need to be kept (sent, sent by fallback, cancelled) are deleted after this.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>Deletes finished items older than <see cref="Retention"/>. Returns how many.</summary>
    public static async Task<int> CleanupAsync(ApplicationDbContext db, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var cutoff = now - Retention;
        var finished = db.BackofficeOutboxItems.Where(i =>
            (i.Status == BackofficeOutboxStatus.Sent
             || i.Status == BackofficeOutboxStatus.FallbackSent
             || i.Status == BackofficeOutboxStatus.Cancelled)
            && i.CreatedAt < cutoff);

        if (db.Database.IsRelational())
        {
            return await finished.ExecuteDeleteAsync(cancellationToken);
        }

        var rows = await finished.ToListAsync(cancellationToken);
        db.BackofficeOutboxItems.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }
}
