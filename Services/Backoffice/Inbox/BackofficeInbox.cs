using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DotNetSigningServer.Services.Backoffice.Inbox;

/// <summary>
/// Stores incoming events once per <c>webhook-id</c>, whether they come by webhook or by
/// polling. On PostgreSQL a single <c>INSERT … ON CONFLICT ("WebhookId") DO NOTHING</c>, so
/// two deliveries racing each other (or two app instances) still produce one row. Other
/// providers (InMemory in tests) use a check-then-insert fallback.
/// </summary>
public sealed class BackofficeInbox
{
    public const int MaxWebhookIdLength = 128;
    public const int MaxTypeLength = 64;

    private readonly ApplicationDbContext _db;
    private readonly TimeProvider _time;

    public BackofficeInbox(ApplicationDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    /// <summary>True when id and type fit the inbox columns.</summary>
    public static bool IsStorable(string? webhookId, string? type) =>
        !string.IsNullOrEmpty(webhookId) && webhookId.Length <= MaxWebhookIdLength
        && !string.IsNullOrEmpty(type) && type.Length <= MaxTypeLength;

    /// <summary>Stores the event unless one with the same id is already there. True when a row was added.</summary>
    public Task<bool> AddIfNewAsync(string webhookId, string type, JsonElement data, string source, CancellationToken cancellationToken = default) =>
        AddIfNewAsync(webhookId, type, data.ValueKind == JsonValueKind.Undefined ? "{}" : data.GetRawText(), source, cancellationToken);

    /// <inheritdoc cref="AddIfNewAsync(string, string, JsonElement, string, CancellationToken)"/>
    public async Task<bool> AddIfNewAsync(string webhookId, string type, string dataJson, string source, CancellationToken cancellationToken = default)
    {
        if (!IsStorable(webhookId, type))
            throw new ArgumentException($"Event id must be 1–{MaxWebhookIdLength} and type 1–{MaxTypeLength} characters.");

        var now = _time.GetUtcNow();
        var item = new BackofficeWebhookInboxItem
        {
            WebhookId = webhookId,
            Type = type,
            PayloadJson = dataJson,
            Source = source,
            ReceivedAt = now,
            NextAttemptAt = now,
        };

        if (_db.Database.IsNpgsql())
        {
            var t = Names.Of(_db);
            var sql = $$"""
                INSERT INTO {{t.Table}} ({{t.Id}}, {{t.WebhookId}}, {{t.Type}}, {{t.PayloadJson}}, {{t.Source}}, {{t.ReceivedAt}}, {{t.Attempts}}, {{t.NextAttemptAt}})
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 0, {5})
                ON CONFLICT ({{t.WebhookId}}) DO NOTHING
                """;
            var rows = await _db.Database.ExecuteSqlRawAsync(
                sql,
                new object[] { item.Id, item.WebhookId, item.Type, item.PayloadJson, item.Source, now.ToUniversalTime() },
                cancellationToken);
            return rows == 1;
        }

        if (await _db.BackofficeWebhookInboxItems.AnyAsync(i => i.WebhookId == webhookId, cancellationToken))
            return false;
        _db.BackofficeWebhookInboxItems.Add(item);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            _db.Entry(item).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>Quoted, schema-qualified table and column names from the EF model.</summary>
    private sealed record Names(
        string Table, string Id, string WebhookId, string Type, string PayloadJson, string Source,
        string ReceivedAt, string Attempts, string NextAttemptAt)
    {
        public static Names Of(DbContext db)
        {
            var entity = db.Model.FindEntityType(typeof(BackofficeWebhookInboxItem))!;
            var store = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            string Column(string property) => Quote(entity.FindProperty(property)!.GetColumnName(store)!);
            var schema = entity.GetSchema();
            var table = (schema is null ? "" : Quote(schema) + ".") + Quote(entity.GetTableName()!);
            return new Names(
                table,
                Column(nameof(BackofficeWebhookInboxItem.Id)),
                Column(nameof(BackofficeWebhookInboxItem.WebhookId)),
                Column(nameof(BackofficeWebhookInboxItem.Type)),
                Column(nameof(BackofficeWebhookInboxItem.PayloadJson)),
                Column(nameof(BackofficeWebhookInboxItem.Source)),
                Column(nameof(BackofficeWebhookInboxItem.ReceivedAt)),
                Column(nameof(BackofficeWebhookInboxItem.Attempts)),
                Column(nameof(BackofficeWebhookInboxItem.NextAttemptAt)));
        }

        private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }
}
