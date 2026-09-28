using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <summary>
/// Claims due outbox items so that exactly one dispatcher (of possibly several app
/// instances) sends each of them: a short lease in <c>LockedUntil</c>, taken with
/// <c>FOR UPDATE SKIP LOCKED</c> on PostgreSQL. Other providers (InMemory in tests) get a
/// plain LINQ equivalent without the cross-instance guarantee.
/// </summary>
internal static class OutboxClaim
{
    public const int BatchSize = 20;

    /// <summary>How long a claim holds; the dispatcher stops using a batch well before that.</summary>
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(2);

    /// <summary>Up to <see cref="BatchSize"/> due items, critical first, then oldest first. Tracked.</summary>
    /// <param name="pausedKindPrefixes">Kinds starting with one of these are left alone (their module is not On).</param>
    public static async Task<List<BackofficeOutboxItem>> ClaimDueAsync(
        ApplicationDbContext db, DateTimeOffset now, CancellationToken cancellationToken,
        IReadOnlyList<string>? pausedKindPrefixes = null)
    {
        var paused = pausedKindPrefixes ?? Array.Empty<string>();
        List<BackofficeOutboxItem> items;
        if (db.Database.IsNpgsql())
        {
            var t = Names.Of(db);
            var parameters = new List<object> { now.ToUniversalTime(), (now + LockDuration).ToUniversalTime() };
            var pausedSql = "";
            foreach (var prefix in paused)
            {
                pausedSql += $" AND {t.Kind} NOT LIKE {{{parameters.Count}}}";
                parameters.Add(LikePrefix(prefix));
            }
            var sql = $$"""
                UPDATE {{t.Table}} SET {{t.LockedUntil}} = {1}
                WHERE {{t.Id}} IN (
                    SELECT {{t.Id}} FROM {{t.Table}}
                    WHERE {{t.Status}} = '{{BackofficeOutboxStatus.Pending}}'
                      AND {{t.NextAttemptAt}} <= {0}
                      AND ({{t.LockedUntil}} IS NULL OR {{t.LockedUntil}} < {0}){{pausedSql}}
                    ORDER BY {{t.Critical}} DESC, {{t.CreatedAt}}
                    LIMIT {{BatchSize}}
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """;
            items = await db.BackofficeOutboxItems
                .FromSqlRaw(sql, parameters.ToArray())
                .ToListAsync(cancellationToken);
        }
        else
        {
            var due = await db.BackofficeOutboxItems
                .Where(i => i.Status == BackofficeOutboxStatus.Pending
                            && i.NextAttemptAt <= now
                            && (i.LockedUntil == null || i.LockedUntil < now))
                .OrderByDescending(i => i.Critical).ThenBy(i => i.CreatedAt)
                .ToListAsync(cancellationToken);
            items = due
                .Where(i => !paused.Any(p => i.Kind.StartsWith(p, StringComparison.Ordinal)))
                .Take(BatchSize)
                .ToList();
            foreach (var item in items) item.LockedUntil = now + LockDuration;
            await db.SaveChangesAsync(cancellationToken);
        }

        return items.OrderByDescending(i => i.Critical).ThenBy(i => i.CreatedAt).ToList();
    }

    /// <summary>
    /// Claims critical items of <paramref name="kind"/> created at or after
    /// <paramref name="notBefore"/> that should go out by the direct fallback: Blocked ones, and
    /// Pending ones created at or before <paramref name="pendingCreatedBefore"/> or whose last
    /// attempt found the service unreachable or failing (network error, 5xx). Oldest first. Tracked.
    /// </summary>
    public static async Task<List<BackofficeOutboxItem>> ClaimForFallbackAsync(
        ApplicationDbContext db, string kind, DateTimeOffset now, DateTimeOffset pendingCreatedBefore,
        DateTimeOffset notBefore, int limit, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
        {
            var t = Names.Of(db);
            var sql = $$"""
                UPDATE {{t.Table}} SET {{t.LockedUntil}} = {1}
                WHERE {{t.Id}} IN (
                    SELECT {{t.Id}} FROM {{t.Table}}
                    WHERE {{t.Critical}}
                      AND {{t.Kind}} = {2}
                      AND {{t.CreatedAt}} >= {3}
                      AND ({{t.LockedUntil}} IS NULL OR {{t.LockedUntil}} < {0})
                      AND ({{t.Status}} = '{{BackofficeOutboxStatus.Blocked}}'
                           OR ({{t.Status}} = '{{BackofficeOutboxStatus.Pending}}'
                               AND ({{t.CreatedAt}} <= {4} OR {{t.LastError}} LIKE {5} OR {{t.LastError}} LIKE {6})))
                    ORDER BY {{t.CreatedAt}}
                    LIMIT {{limit}}
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """;
            var claimed = await db.BackofficeOutboxItems
                .FromSqlRaw(
                    sql,
                    now.ToUniversalTime(),
                    (now + LockDuration).ToUniversalTime(),
                    kind,
                    notBefore.ToUniversalTime(),
                    pendingCreatedBefore.ToUniversalTime(),
                    LikePrefix(OutboxAttemptResult.NetworkErrorPrefix),
                    LikePrefix("HTTP 5"))
                .ToListAsync(cancellationToken);
            return claimed.OrderBy(i => i.CreatedAt).ToList();
        }

        var candidates = await db.BackofficeOutboxItems
            .Where(i => i.Critical
                        && i.Kind == kind
                        && i.CreatedAt >= notBefore
                        && (i.LockedUntil == null || i.LockedUntil < now)
                        && (i.Status == BackofficeOutboxStatus.Blocked || i.Status == BackofficeOutboxStatus.Pending))
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(cancellationToken);
        var items = candidates
            .Where(i => i.Status == BackofficeOutboxStatus.Blocked
                        || i.CreatedAt <= pendingCreatedBefore
                        || LastAttemptFoundServiceDown(i.LastError))
            .Take(limit)
            .ToList();
        foreach (var item in items) item.LockedUntil = now + LockDuration;
        await db.SaveChangesAsync(cancellationToken);
        return items;
    }

    /// <summary>The stored error of an attempt that got no answer or a 5xx.</summary>
    internal static bool LastAttemptFoundServiceDown(string? lastError) =>
        lastError != null
        && (lastError.StartsWith(OutboxAttemptResult.NetworkErrorPrefix, StringComparison.Ordinal)
            || lastError.StartsWith("HTTP 5", StringComparison.Ordinal));

    /// <summary>A LIKE pattern matching values that start with <paramref name="prefix"/>.</summary>
    private static string LikePrefix(string prefix) =>
        prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    /// <summary>
    /// Claims one pending item regardless of its next attempt time (an explicit "send now").
    /// Null when it does not exist (yet), is not pending, or someone else holds it.
    /// </summary>
    public static async Task<BackofficeOutboxItem?> ClaimOneAsync(
        ApplicationDbContext db, Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
        {
            var t = Names.Of(db);
            var sql = $$"""
                UPDATE {{t.Table}} SET {{t.LockedUntil}} = {1}
                WHERE {{t.Id}} IN (
                    SELECT {{t.Id}} FROM {{t.Table}}
                    WHERE {{t.Id}} = {2}
                      AND {{t.Status}} = '{{BackofficeOutboxStatus.Pending}}'
                      AND ({{t.LockedUntil}} IS NULL OR {{t.LockedUntil}} < {0})
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """;
            var claimed = await db.BackofficeOutboxItems
                .FromSqlRaw(sql, now.ToUniversalTime(), (now + LockDuration).ToUniversalTime(), id)
                .ToListAsync(cancellationToken);
            return claimed.SingleOrDefault();
        }

        var item = await db.BackofficeOutboxItems.FirstOrDefaultAsync(
            i => i.Id == id
                 && i.Status == BackofficeOutboxStatus.Pending
                 && (i.LockedUntil == null || i.LockedUntil < now),
            cancellationToken);
        if (item == null) return null;
        item.LockedUntil = now + LockDuration;
        await db.SaveChangesAsync(cancellationToken);
        return item;
    }

    /// <summary>Quoted, schema-qualified table and column names from the EF model.</summary>
    private sealed record Names(
        string Table, string Id, string Kind, string Status, string NextAttemptAt, string LockedUntil, string Critical,
        string CreatedAt, string LastError)
    {
        public static Names Of(DbContext db)
        {
            var entity = db.Model.FindEntityType(typeof(BackofficeOutboxItem))!;
            var store = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            string Column(string property) => Quote(entity.FindProperty(property)!.GetColumnName(store)!);
            var schema = entity.GetSchema();
            var table = (schema is null ? "" : Quote(schema) + ".") + Quote(entity.GetTableName()!);
            return new Names(
                table,
                Column(nameof(BackofficeOutboxItem.Id)),
                Column(nameof(BackofficeOutboxItem.Kind)),
                Column(nameof(BackofficeOutboxItem.Status)),
                Column(nameof(BackofficeOutboxItem.NextAttemptAt)),
                Column(nameof(BackofficeOutboxItem.LockedUntil)),
                Column(nameof(BackofficeOutboxItem.Critical)),
                Column(nameof(BackofficeOutboxItem.CreatedAt)),
                Column(nameof(BackofficeOutboxItem.LastError)));
        }

        private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }
}
