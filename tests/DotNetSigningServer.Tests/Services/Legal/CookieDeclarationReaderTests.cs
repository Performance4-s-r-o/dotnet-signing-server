using System.Net;
using System.Text.Json;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Legal;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services.Legal;

/// <summary>Docs module On, with the service stubbed by <see cref="StubServiceHandler"/>.</summary>
public class CookieDeclarationReaderTests
{
    /// <summary>A <c>CookieDeclaration</c> body as served by <c>GET /v1/cookie-declaration</c>.</summary>
    internal static string DeclarationJson(int version = 2, string locale = "cs", params string[] names)
    {
        var cookies = (names.Length == 0 ? AuditedCookies.Names : names).Select(name => new Dictionary<string, object?>
        {
            ["name"] = name,
            ["provider"] = "Performance4",
            ["category"] = "necessary",
            ["purpose"] = $"Purpose of {name}",
            ["duration"] = "Relace",
            ["party"] = "first",
        }).ToList<object>();
        cookies.Add(new Dictionary<string, object?> { ["name"] = "", ["provider"] = "x" });
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["product"] = new { name = "Performance4PDF", slug = "p4pdf", color = (string?)null },
            ["version"] = version,
            ["published_at"] = "2026-09-28T10:00:00Z",
            ["locale"] = locale,
            ["locales"] = new[] { "cs", "en" },
            ["categories"] = Array.Empty<string>(),
            ["reprompt_months"] = 12,
            ["respect_gpc"] = true,
            ["consent_mode"] = false,
            ["policy_url"] = "https://backoffice.test/l/p4pdf/cookies",
            ["texts"] = new { title = "Cookies", description = "x", settings_description = (string?)null, categories = new { } },
            ["cookies"] = cookies,
        });
    }

    private static void EnqueueDeclaration(LegalDocsTestHost host, string json, string etag = "\"cd-1\"") =>
        host.Service.Responses.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            return Task.FromResult(response);
        });

    private static CookieDeclarationReader Reader(LegalDocsTestHost host) =>
        Assert.IsType<CookieDeclarationReader>(host.Services.GetRequiredService<ICookieDeclarationSource>());

    private static async Task<CookieDeclarationSnapshot?> GetAsync(CookieDeclarationReader reader, string locale = "cs")
    {
        var result = await reader.GetAsync(locale);
        if (reader.PendingRefresh != null) await reader.PendingRefresh;
        return result;
    }

    private static Task<string?> StoredAsync(LegalDocsTestHost host, string locale) =>
        host.WithDbAsync(db => BackofficeStateStore.GetAsync(db, CookieDeclarationReader.StateKey(locale)));

    [Fact]
    public void FromResponse_MapsTheDeclaration_AndSkipsNamelessCookies()
    {
        var fetchedAt = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        var snapshot = CookieDeclarationSnapshot.FromResponse("cs", DeclarationJson(), "\"cd-1\"", fetchedAt);

        Assert.Equal(2, snapshot.Version);
        Assert.Equal("cs", snapshot.Locale);
        Assert.Empty(snapshot.Categories);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero), snapshot.PublishedAt);
        Assert.Equal("\"cd-1\"", snapshot.ETag);
        Assert.Equal(fetchedAt, snapshot.FetchedAt);
        Assert.Equal(AuditedCookies.Names, snapshot.Cookies.Select(c => c.Name));
        var first = snapshot.Cookies[0];
        Assert.Equal(new CookieDeclarationCookie(".AspNetCore.Cookies", "Performance4", "necessary",
            "Purpose of .AspNetCore.Cookies", "Relace", "first"), first);
    }

    [Theory]
    [InlineData("""{"cookies":[]}""")]
    [InlineData("""{"version":0,"cookies":[]}""")]
    [InlineData("""{"version":3}""")]
    [InlineData("""[]""")]
    public void FromResponse_WithoutVersionOrCookies_Throws(string body)
    {
        Assert.ThrowsAny<JsonException>(() => CookieDeclarationSnapshot.FromResponse("en", body, null, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Snapshot_RoundTrips_AndUnreadableIsNull()
    {
        var snapshot = CookieDeclarationSnapshot.FromResponse("en", DeclarationJson(locale: "en"), "\"e\"", DateTimeOffset.UnixEpoch);

        var back = CookieDeclarationSnapshot.TryDeserialize(snapshot.Serialize());

        Assert.NotNull(back);
        Assert.Equal(snapshot.Version, back!.Version);
        Assert.Equal(snapshot.Cookies, back.Cookies);
        Assert.Equal("\"e\"", back.ETag);
        Assert.Null(CookieDeclarationSnapshot.TryDeserialize("not json"));
        Assert.Null(CookieDeclarationSnapshot.TryDeserialize(""));
    }

    [Fact]
    public async Task ColdStart_ReturnsNothing_ThenStoresAndServesTheDeclaration()
    {
        using var host = new LegalDocsTestHost();
        EnqueueDeclaration(host, DeclarationJson());
        var reader = Reader(host);

        // Nothing in memory or the database: no table from the service yet, fetch in the background.
        Assert.Null(await GetAsync(reader));
        Assert.Equal("https://backoffice.test/v1/cookie-declaration?locale=cs", host.Request(0).RequestUri!.ToString());

        var stored = CookieDeclarationSnapshot.TryDeserialize(await StoredAsync(host, "cs"));
        Assert.NotNull(stored);
        Assert.Equal(2, stored!.Version);
        Assert.Equal(4, stored.Cookies.Count);

        // Within the TTL: from memory, no new request.
        var second = await GetAsync(reader);
        Assert.Equal(2, second!.Version);
        Assert.Equal(1, host.RequestCount);
    }

    [Fact]
    public async Task ServiceDown_FallsBackToTheSnapshot_AndBacksOff()
    {
        using var host = new LegalDocsTestHost();
        var stored = CookieDeclarationSnapshot.FromResponse("cs", DeclarationJson(version: 1), "\"old\"", host.Time.Now);
        await host.WithDbAsync(async db =>
        {
            await BackofficeStateStore.SetAsync(db, CookieDeclarationReader.StateKey("cs"), stored.Serialize(), host.Time.Now);
            return 0;
        });
        host.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        var reader = Reader(host);

        var result = await GetAsync(reader);

        Assert.Equal(1, result!.Version);
        Assert.Equal(AuditedCookies.Names, result.Cookies.Select(c => c.Name));
        Assert.True(reader.InBackoff);
        // The revalidation was conditional on the stored ETag.
        Assert.Equal("\"old\"", host.Request(0).Headers.IfNoneMatch.Single().ToString());

        // During the backoff the page does not ask again, and still gets the snapshot.
        Assert.Equal(1, (await reader.GetAsync("cs"))!.Version);
        Assert.Equal(1, host.RequestCount);
    }

    [Fact]
    public async Task StaleCopy_IsRevalidated_AndNotModifiedKeepsIt()
    {
        using var host = new LegalDocsTestHost();
        EnqueueDeclaration(host, DeclarationJson());
        var reader = Reader(host);
        await reader.RefreshAsync("cs", force: false, CancellationToken.None);

        host.Time.Advance(reader.Ttl + TimeSpan.FromSeconds(1));
        host.EnqueueStatus(HttpStatusCode.NotModified);
        var result = await GetAsync(reader);

        Assert.Equal(2, result!.Version);
        Assert.Equal(2, host.RequestCount);
        Assert.Equal("\"cd-1\"", host.Request(1).Headers.IfNoneMatch.Single().ToString());
        Assert.False(reader.InBackoff);
    }

    [Fact]
    public async Task NothingPublished_IsAnAnswer_NotAnOutage()
    {
        using var host = new LegalDocsTestHost();
        host.EnqueueStatus(HttpStatusCode.NotFound);
        var reader = Reader(host);

        Assert.Null(await reader.RefreshAsync("en", force: false, CancellationToken.None));
        Assert.False(reader.InBackoff);
        Assert.Null(await StoredAsync(host, "en"));
    }

    [Fact]
    public async Task InvalidBody_Throws_AndKeepsTheStoredCopy()
    {
        using var host = new LegalDocsTestHost();
        EnqueueDeclaration(host, DeclarationJson());
        var reader = Reader(host);
        await reader.RefreshAsync("cs", force: false, CancellationToken.None);

        host.Service.Enqueue(HttpStatusCode.OK, """{"unexpected":true}""");
        await Assert.ThrowsAsync<HttpRequestException>(() => reader.RefreshAsync("cs", force: true, CancellationToken.None));

        Assert.Equal(2, CookieDeclarationSnapshot.TryDeserialize(await StoredAsync(host, "cs"))!.Version);
    }

    [Fact]
    public async Task PublishedEvent_RefetchesEveryLanguage_IgnoringTheETag()
    {
        using var host = new LegalDocsTestHost();
        EnqueueDeclaration(host, DeclarationJson(version: 1, locale: "en"));
        EnqueueDeclaration(host, DeclarationJson(version: 1, locale: "cs"));
        var reader = Reader(host);
        await reader.RefreshAllAsync(force: false, CancellationToken.None);

        EnqueueDeclaration(host, DeclarationJson(version: 2, locale: "en", ".AspNetCore.Cookies"), "\"cd-2\"");
        EnqueueDeclaration(host, DeclarationJson(version: 2, locale: "cs", ".AspNetCore.Cookies"), "\"cd-2\"");
        using (var scope = host.Services.CreateScope())
        {
            var handler = scope.ServiceProvider.GetServices<IBackofficeEventHandler>().OfType<CookieDeclarationEventsHandler>().Single();
            Assert.Contains(BackofficeEventTypes.CookieDeclarationPublished, handler.Types);
            using var data = JsonDocument.Parse("""{"version":2,"categories":[],"locales":["cs","en"],"cookies":1,"reprompt":true}""");
            await handler.HandleAsync(new BackofficeEvent("evt_1", BackofficeEventTypes.CookieDeclarationPublished,
                data.RootElement.Clone(), "webhook", host.Time.Now, 1), CancellationToken.None);
        }

        Assert.Equal(4, host.RequestCount);
        Assert.Empty(host.Request(2).Headers.IfNoneMatch);
        Assert.Empty(host.Request(3).Headers.IfNoneMatch);
        foreach (var locale in LegalLocales.Snapshot)
        {
            var current = await reader.GetAsync(locale);
            Assert.Equal(2, current!.Version);
            Assert.Equal(".AspNetCore.Cookies", Assert.Single(current.Cookies).Name);
            Assert.Equal(2, CookieDeclarationSnapshot.TryDeserialize(await StoredAsync(host, locale))!.Version);
        }
    }

    [Fact]
    public async Task PublishedEvent_FailingService_Throws_SoTheInboxRetries()
    {
        using var host = new LegalDocsTestHost();
        host.EnqueueStatus(HttpStatusCode.BadGateway);
        using var scope = host.Services.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IBackofficeEventHandler>().OfType<CookieDeclarationEventsHandler>().Single();
        using var data = JsonDocument.Parse("{}");

        await Assert.ThrowsAsync<HttpRequestException>(() => handler.HandleAsync(
            new BackofficeEvent("evt_2", BackofficeEventTypes.CookieDeclarationPublished, data.RootElement.Clone(), "poll", host.Time.Now, 1),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("Shadow")]
    public async Task OffAndShadow_HaveNoDeclaration_AndNoHandler(string mode)
    {
        using var host = new LegalDocsTestHost(mode);

        var source = Assert.IsType<NoCookieDeclarationSource>(host.Services.GetRequiredService<ICookieDeclarationSource>());
        Assert.Null(await source.GetAsync("cs"));
        using var scope = host.Services.CreateScope();
        Assert.DoesNotContain(scope.ServiceProvider.GetServices<IBackofficeEventHandler>(), h => h is CookieDeclarationEventsHandler);
        Assert.Equal(0, host.RequestCount);
    }
}
