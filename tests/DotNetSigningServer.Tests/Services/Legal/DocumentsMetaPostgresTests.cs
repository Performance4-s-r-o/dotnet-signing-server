using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Documents;
using DotNetSigningServer.Services.Legal;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Tests.Services.Legal;

/// <summary>Concurrent <c>docs:meta</c> writers on a real PostgreSQL.</summary>
[Trait("Category", "Db")]
public class DocumentsMetaPostgresTests : IClassFixture<OutboxPostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly OutboxPostgresFixture _pg;

    public DocumentsMetaPostgresTests(OutboxPostgresFixture pg)
    {
        _pg = pg;
    }

    private static DocumentsMeta With(DocumentsMeta? existing, string type)
    {
        var documents = existing?.Documents is { } stored
            ? new Dictionary<string, DocumentMeta>(stored, StringComparer.Ordinal)
            : new Dictionary<string, DocumentMeta>(StringComparer.Ordinal);
        // Widens the read-modify-write window: without the lock the writers overwrite each other.
        Thread.Sleep(25);
        documents[type] = new DocumentMeta(true, 1, 1, null);
        return new DocumentsMeta(Now, documents);
    }

    [DockerFact]
    public async Task ConcurrentUpdatesOfDifferentTypes_KeepEveryEntry()
    {
        await using (var clear = _pg.CreateContext())
            await clear.BackofficeStates.Where(s => s.Key == BackofficeStateKeys.DocsMeta).ExecuteDeleteAsync();

        var types = Enumerable.Range(0, 8).Select(i => $"type_{i}").ToArray();
        await Task.WhenAll(types.Select(type => Task.Run(async () =>
        {
            await using var db = _pg.CreateContext();
            await DocumentsMetaUpdater.UpdateAsync(db, existing => With(existing, type), Now, CancellationToken.None);
        })));

        await using var read = _pg.CreateContext();
        var meta = await DocumentsMetaUpdater.ReadAsync(read);
        Assert.Equal(types, meta!.Documents.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }
}
