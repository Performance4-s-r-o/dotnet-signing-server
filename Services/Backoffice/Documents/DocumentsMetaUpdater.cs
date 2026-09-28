using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Legal;

namespace DotNetSigningServer.Services.Backoffice.Documents;

/// <summary>
/// Maintains the <c>docs:meta</c> state row from <c>GET /v1/documents</c> and
/// <c>GET /v1/documents/{type}/versions</c>. Background only (warm-up, resync, document
/// events); readers (consents) use <see cref="ReadAsync"/> and never call the service.
/// Throws when the service fails, so callers retry.
/// </summary>
public sealed class DocumentsMetaUpdater
{
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

        var meta = new DocumentsMeta(now, documents);
        await BackofficeStateStore.SetAsync(_db, BackofficeStateKeys.DocsMeta, meta.ToJson(), now, cancellationToken);
        return meta;
    }

    /// <summary>Rebuilds the entry of one document; the others stay as stored.</summary>
    public async Task<DocumentsMeta> RefreshTypeAsync(string type, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var summary = (await _client.ListAsync(cancellationToken)).FirstOrDefault(d => d.Type == type);
        var existing = await ReadAsync(_db, cancellationToken);
        var documents = existing?.Documents is { } stored
            ? new Dictionary<string, DocumentMeta>(stored, StringComparer.Ordinal)
            : new Dictionary<string, DocumentMeta>(StringComparer.Ordinal);

        if (summary is null)
        {
            documents.Remove(type);
        }
        else
        {
            var versions = await _client.VersionsAsync(type, cancellationToken);
            documents[type] = DocumentRequirements.Meta(summary, versions, now);
        }

        var meta = new DocumentsMeta(now, documents);
        await BackofficeStateStore.SetAsync(_db, BackofficeStateKeys.DocsMeta, meta.ToJson(), now, cancellationToken);
        return meta;
    }
}
