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
    public static async Task<List<BackofficeOutboxItem>> ClaimDueAsync(
        ApplicationDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<BackofficeOutboxItem> items;
        if (db.Database.IsNpgsql())
        {
            var t = Names.Of(db);
            var sql = $$"""
                UPDATE {{t.Table}} SET {{t.LockedUntil}} = {1}
                WHERE {{t.Id}} IN (
                    SELECT {{t.Id}} FROM {{t.Table}}
                    WHERE {{t.Status}} = '{{BackofficeOutboxStatus.Pending}}'
                      AND {{t.NextAttemptAt}} <= {0}
                      AND ({{t.LockedUntil}} IS NULL OR {{t.LockedUntil}} < {0})
                    ORDER BY {{t.Critical}} DESC, {{t.CreatedAt}}
                    LIMIT {{BatchSize}}
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """;
            items = await db.BackofficeOutboxItems
                .FromSqlRaw(sql, now.ToUniversalTime(), (now + LockDuration).ToUniversalTime())
                .ToListAsync(cancellationToken);
        }
        else
        {
            items = await db.BackofficeOutboxItems
                .Where(i => i.Status == BackofficeOutboxStatus.Pending
                            && i.NextAttemptAt <= now
                            && (i.LockedUntil == null || i.LockedUntil < now))
                .OrderByDescending(i => i.Critical).ThenBy(i => i.CreatedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            foreach (var item in items) item.LockedUntil = now + LockDuration;
            await db.SaveChangesAsync(cancellationToken);
        }

        return items.OrderByDescending(i => i.Critical).ThenBy(i => i.CreatedAt).ToList();
    }

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
        string Table, string Id, string Status, string NextAttemptAt, string LockedUntil, string Critical, string CreatedAt)
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
                Column(nameof(BackofficeOutboxItem.Status)),
                Column(nameof(BackofficeOutboxItem.NextAttemptAt)),
                Column(nameof(BackofficeOutboxItem.LockedUntil)),
                Column(nameof(BackofficeOutboxItem.Critical)),
                Column(nameof(BackofficeOutboxItem.CreatedAt)));
        }

        private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }
}
