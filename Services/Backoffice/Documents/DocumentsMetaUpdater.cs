using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Legal;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Services.Backoffice.Documents;

/// <summary>
/// Maintains the <c>docs:meta</c> state row from <c>GET /v1/documents</c> and
/// <c>GET /v1/documents/{type}/versions</c>. Background only (warm-up, resync, document
/// events); readers (consents) use <see cref="ReadAsync"/> and never call the service.
/// Throws when the service fails, so callers retry.
/// <para>
/// <c>docs:meta</c> is one JSON blob, so every write is a read-modify-write. On PostgreSQL it
/// runs in a transaction holding <see cref="AdvisoryLockKey"/> (<c>pg_advisory_xact_lock</c>),
/// which serialises writers across instances (webhook handling vs. polling catch-up vs. resync)
/// and keeps an update of one type from dropping another type's entry. The service is called
/// before the lock is taken, so no HTTP call happens while it is held.
/// </para>
/// </summary>
public sealed class DocumentsMetaUpdater
{
    /// <summary>
    /// Key of the transaction-scoped advisory lock around <c>docs:meta</c> writes
    /// ("P4BODOCM" as ASCII; distinct from the polling lock).
    /// </summary>
    public const long AdvisoryLockKey = 0x5034_424F_444F_434D;

    private readonly BackofficeDocumentsClient _client;
    private readonly ApplicationDbContext _db;
    private readonly TimeProvider _time;

    public DocumentsMetaUpdater(BackofficeDocumentsClient client, ApplicationDbContext db, TimeProvider time)
    {
        _client = client;
        _db = db;
        _time = time;
    }

    /// <summary>The stored <c>docs:meta</c>; null before the first successful update.</summary>
    public static async Task<DocumentsMeta?> ReadAsync(ApplicationDbContext db, CancellationToken cancellationToken = default) =>
        DocumentsMeta.Parse(await BackofficeStateStore.GetAsync(db, BackofficeStateKeys.DocsMeta, cancellationToken));

    /// <summary>Rebuilds the entries of every document of the product.</summary>
    public async Task<DocumentsMeta> RefreshAllAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var documents = new Dictionary<string, DocumentMeta>(StringComparer.Ordinal);
        foreach (var summary in await _client.ListAsync(cancellationToken))
        {
            var versions = await _client.VersionsAsync(summary.Type, cancellationToken);
            documents[summary.Type] = DocumentRequirements.Meta(summary, versions, now);
        }

        return await UpdateAsync(_db, _ => new DocumentsMeta(now, documents), now, cancellationToken);
    }

    /// <summary>Rebuilds the entry of one document; the others stay as stored.</summary>
    public async Task<DocumentsMeta> RefreshTypeAsync(string type, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var summary = (await _client.ListAsync(cancellationToken)).FirstOrDefault(d => d.Type == type);
        DocumentMeta? entry = null;
        if (summary is not null)
        {
            var versions = await _client.VersionsAsync(type, cancellationToken);
            entry = DocumentRequirements.Meta(summary, versions, now);
        }

        return await UpdateAsync(_db, existing =>
        {
            var documents = existing?.Documents is { } stored
                ? new Dictionary<string, DocumentMeta>(stored, StringComparer.Ordinal)
                : new Dictionary<string, DocumentMeta>(StringComparer.Ordinal);
            if (entry is null) documents.Remove(type);
            else documents[type] = entry;
            return new DocumentsMeta(now, documents);
        }, now, cancellationToken);
    }

    /// <summary>
    /// Reads the stored <c>docs:meta</c>, applies <paramref name="change"/> and writes the result,
    /// exclusively across instances on PostgreSQL (see the class remarks). <paramref name="change"/>
    /// must be pure: it may run again when the execution strategy retries.
    /// </summary>
    internal static async Task<DocumentsMeta> UpdateAsync(
        ApplicationDbContext db, Func<DocumentsMeta?, DocumentsMeta> change, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
        {
            var result = change(await ReadAsync(db, cancellationToken));
            await BackofficeStateStore.SetAsync(db, BackofficeStateKeys.DocsMeta, result.ToJson(), now, cancellationToken);
            return result;
        }

        if (db.Database.CurrentTransaction != null)
        {
            // The caller's transaction: the lock lasts until it ends.
            return await LockedUpdateAsync(db, change, now, cancellationToken);
        }

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var result = await LockedUpdateAsync(db, change, now, ct);
            await tx.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }

    private static async Task<DocumentsMeta> LockedUpdateAsync(
        ApplicationDbContext db, Func<DocumentsMeta?, DocumentsMeta> change, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Transaction-scoped: released on commit or rollback.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", cancellationToken);
        var result = change(await ReadAsync(db, cancellationToken));
        await BackofficeStateStore.SetAsync(db, BackofficeStateKeys.DocsMeta, result.ToJson(), now, cancellationToken);
        return result;
    }
}
