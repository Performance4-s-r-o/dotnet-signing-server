using System.Net;
using System.Text.Json;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Documents;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Legal;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services.Legal;

/// <summary>Document events, docs:meta and the resync, against the stubbed service.</summary>
public class DocumentEventsTests
{
    private const string ListJson = """
        {"data":[{"type":"terms","name":"Terms","requires_consent":true,"revocable":false,"legal_weight":false,"notice_days":30,
          "current":{"version":2,"status":"effective","change_kind":"material","effective_from":"2026-09-01T00:00:00Z","published_at":"2026-08-01T00:00:00Z","summary":null,"locales":[]},
          "upcoming":{"version":3,"status":"scheduled","change_kind":"notice","effective_from":"2026-11-01T00:00:00Z","published_at":"2026-09-28T00:00:00Z","summary":"New address","locales":[]},
          "url":"https://backoffice.test/l/p4pdf/terms"}]}
        """;

    private const string VersionsJson = """
        {"data":[
          {"version":3,"status":"scheduled","change_kind":"notice","effective_from":"2026-11-01T00:00:00Z","published_at":"2026-09-28T00:00:00Z","summary":"New address","locales":[]},
          {"version":2,"status":"effective","change_kind":"material","effective_from":"2026-09-01T00:00:00Z","published_at":"2026-08-01T00:00:00Z","summary":null,"locales":[]},
          {"version":1,"status":"superseded","change_kind":"material","effective_from":"2026-01-01T00:00:00Z","published_at":"2025-12-01T00:00:00Z","summary":null,"locales":[]}]}
        """;

    private static BackofficeEvent Event(string type, string documentType = "terms") => new(
        "msg_1", type,
        JsonDocument.Parse($$"""{"document":{"type":"{{documentType}}","name":"Terms","requires_consent":true,"revocable":false},"version":2}""").RootElement,
        BackofficeInboxSource.Webhook, DateTimeOffset.UtcNow, 1);

    private static Task HandleAsync(LegalDocsTestHost host, BackofficeEvent evt) =>
        host.WithScopeAsync(async sp =>
        {
            var handler = sp.GetServices<IBackofficeEventHandler>().OfType<DocumentEventsHandler>().Single();
            await handler.HandleAsync(evt, CancellationToken.None);
            return true;
        });

    private static Task<DocumentsMeta?> MetaAsync(LegalDocsTestHost host) =>
        host.WithDbAsync(db => DocumentsMetaUpdater.ReadAsync(db));

    [Fact]
    public async Task Published_RefetchesBothLocales_UpdatesTheSnapshotAndMeta()
    {
        using var host = new LegalDocsTestHost();
        // An older copy in memory must not survive the event.
        host.EnqueueDocument(LegalDocsTestHost.DocumentJson(version: 1, html: "<p>v1</p>"), etag: "\"v1\"");
        await host.Refresher.RefreshAsync("terms", "en", saveSnapshot: false, CancellationToken.None);

        host.EnqueueDocument(LegalDocsTestHost.DocumentJson(version: 2), etag: "\"v2\"");
        host.EnqueueDocument(LegalDocsTestHost.DocumentJson(version: 2, locale: "cs", title: "Obchodní podmínky", html: "<p>cs</p>"));
        host.Service.Enqueue(HttpStatusCode.OK, ListJson);
        host.Service.Enqueue(HttpStatusCode.OK, VersionsJson);

        await HandleAsync(host, Event(BackofficeEventTypes.DocumentPublished));

        // Unconditional refetch: the memory entry was dropped first.
        Assert.Empty(host.Request(1).Headers.IfNoneMatch);
        Assert.Contains("locale=en", host.Request(1).RequestUri!.Query);
        Assert.Contains("locale=cs", host.Request(2).RequestUri!.Query);
        Assert.Equal("/v1/documents", host.Request(3).RequestUri!.AbsolutePath);
        Assert.Equal("/v1/documents/terms/versions", host.Request(4).RequestUri!.AbsolutePath);

        Assert.Equal(2, host.Cache.Peek("terms", "en")!.Document.Version);
        var rows = await host.RowsAsync();
        Assert.Equal(new[] { "cs", "en" }, rows.Select(r => r.Locale));
        Assert.All(rows, r => Assert.Equal(2, r.Version));

        var meta = (await MetaAsync(host))!.Documents["terms"];
        Assert.True(meta.RequiresConsent);
        Assert.Equal(2, meta.CurrentVersion);
        Assert.Equal(2, meta.RequiredVersion);
        Assert.Equal(3, meta.Upcoming!.Version);
    }

    [Fact]
    public async Task Published_FetchFailure_Throws_SoTheInboxRetries()
    {
        using var host = new LegalDocsTestHost();
        host.EnqueueStatus(HttpStatusCode.BadGateway);

        await Assert.ThrowsAsync<HttpRequestException>(() => HandleAsync(host, Event(BackofficeEventTypes.DocumentPublished)));
        Assert.Empty(await host.RowsAsync());
    }

    [Theory]
    [InlineData(BackofficeEventTypes.DocumentScheduled)]
    [InlineData(BackofficeEventTypes.DocumentUnscheduled)]
    public async Task ScheduledAndUnscheduled_OnlyRebuildTheMeta(string eventType)
    {
        using var host = new LegalDocsTestHost();
        host.Service.Enqueue(HttpStatusCode.OK, ListJson);
        host.Service.Enqueue(HttpStatusCode.OK, VersionsJson);

        await HandleAsync(host, Event(eventType));

        Assert.Equal(2, host.RequestCount);
        Assert.Empty(await host.RowsAsync());
        Assert.Equal(3, (await MetaAsync(host))!.Documents["terms"].Upcoming!.Version);
    }

    [Fact]
    public async Task MetaOfOneType_KeepsTheOtherEntries()
    {
        using var host = new LegalDocsTestHost();
        await host.WithDbAsync(async db =>
        {
            await BackofficeStateStore.SetAsync(db, BackofficeStateKeys.DocsMeta,
                new DocumentsMeta(host.Time.Now, new() { ["dpa"] = new DocumentMeta(true, 1, 1, null) }).ToJson(), host.Time.Now);
            return true;
        });
        host.Service.Enqueue(HttpStatusCode.OK, ListJson);
        host.Service.Enqueue(HttpStatusCode.OK, VersionsJson);

        await HandleAsync(host, Event(BackofficeEventTypes.DocumentScheduled));

        var meta = (await MetaAsync(host))!;
        Assert.Equal(new[] { "dpa", "terms" }, meta.Documents.Keys.OrderBy(k => k));
    }

    [Fact]
    public async Task UnknownDocumentType_OnlyUpdatesTheMeta()
    {
        using var host = new LegalDocsTestHost();
        host.Service.Enqueue(HttpStatusCode.OK, """{"data":[]}""");

        await HandleAsync(host, Event(BackofficeEventTypes.DocumentPublished, documentType: "health_data"));

        Assert.Equal(1, host.RequestCount);
        Assert.Empty(await host.RowsAsync());
    }

    [Fact]
    public async Task EventWithoutADocumentType_IsIgnored()
    {
        using var host = new LegalDocsTestHost();
        var evt = Event(BackofficeEventTypes.DocumentPublished) with { Data = JsonDocument.Parse("{}").RootElement };

        await HandleAsync(host, evt);

        Assert.Equal(0, host.RequestCount);
    }

    [Fact]
    public async Task Resync_FetchesMetaAndEveryDocumentInBothLocales()
    {
        using var host = new LegalDocsTestHost();
        host.Service.Enqueue(HttpStatusCode.OK, ListJson);
        host.Service.Enqueue(HttpStatusCode.OK, VersionsJson);
        foreach (var type in LegalSlugMap.Types)
        {
            host.EnqueueDocument(LegalDocsTestHost.DocumentJson(type: type, version: 1));
            // Only English exists: the service falls back.
            host.EnqueueDocument(LegalDocsTestHost.DocumentJson(type: type, version: 1, fallback: true));
        }

        await host.WithScopeAsync(async sp =>
        {
            var resync = sp.GetServices<IBackofficeResync>().Single();
            Assert.Equal("documents", resync.Name);
            await resync.ResyncAsync("window_clamped", CancellationToken.None);
            return true;
        });

        Assert.Equal(2 + 2 * LegalSlugMap.Types.Count, host.RequestCount);
        var rows = await host.RowsAsync();
        Assert.Equal(LegalSlugMap.Types.Count, rows.Count);
        Assert.All(rows, r => Assert.Equal("en", r.Locale));
        Assert.NotNull((await MetaAsync(host))?.Documents["terms"]);
    }

    [Fact]
    public async Task Resync_NotPublishedTypeIsFine_ButAnOutageThrows()
    {
        using var host = new LegalDocsTestHost();
        host.Service.Enqueue(HttpStatusCode.OK, """{"data":[]}""");
        host.EnqueueStatus(HttpStatusCode.NotFound);          // terms: not in the service
        for (var i = 1; i < LegalSlugMap.Types.Count; i++)
        {
            host.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
            host.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.WithScopeAsync(async sp =>
        {
            await sp.GetRequiredService<DocumentsResync>().ResyncAsync("startup", CancellationToken.None);
            return true;
        }));
        Assert.Contains($"{2 * (LegalSlugMap.Types.Count - 1)} step(s)", error.Message);
    }
}
