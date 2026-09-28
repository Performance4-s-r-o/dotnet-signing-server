using System.Net;
using System.Security.Cryptography;
using System.Text;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Documents;
using DotNetSigningServer.Services.Legal;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetSigningServer.Tests.Services.Legal;

/// <summary>
/// InMemory database + the Docs module wired by <see cref="BackofficeRegistration.AddLegalDocuments"/>
/// in the given mode, with the service replaced by <see cref="StubServiceHandler"/> — no test
/// reaches a real service.
/// </summary>
internal sealed class LegalDocsTestHost : IDisposable
{
    public ServiceProvider Services { get; }
    public StubServiceHandler Service { get; } = new();
    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));

    public LegalDocsTestHost(string docsMode = "On", Action<DbContextOptionsBuilder>? database = null)
    {
        var options = new P4BackofficeProductOptions
        {
            Modules = { Docs = docsMode },
            BaseUrl = OutboxTestHost.BaseUrl,
            SecretKey = OutboxTestHost.SecretKey,
            DisabledReason = BackofficeDisabledReason.None,
        };

        var dbName = "legal-" + Guid.NewGuid();
        var collection = new ServiceCollection();
        collection.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        collection.AddDbContext<ApplicationDbContext>(database ?? (o => o.UseInMemoryDatabase(dbName)));
        collection.AddSingleton<TimeProvider>(Time);
        BackofficeRegistration.AddLegalDocuments(collection, options);
        collection.AddHttpClient(BackofficeDocumentsClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => Service);
        Services = collection.BuildServiceProvider();
    }

    public BackofficeDocumentsCache Cache => Services.GetRequiredService<BackofficeDocumentsCache>();
    public LegalDocumentRefresher Refresher => Services.GetRequiredService<LegalDocumentRefresher>();

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> action) =>
        WithScopeAsync(sp => action(sp.GetRequiredService<ApplicationDbContext>()));

    public Task<List<LegalDocument>> RowsAsync() =>
        WithDbAsync(db => db.LegalDocuments.AsNoTracking().OrderBy(d => d.Slug).ThenBy(d => d.Locale).ThenBy(d => d.Version).ToListAsync());

    public Task SeedAsync(params LegalDocument[] rows) =>
        WithDbAsync(async db =>
        {
            db.LegalDocuments.AddRange(rows);
            return await db.SaveChangesAsync();
        });

    /// <summary>Answers the next request with a document (and an ETag).</summary>
    public void EnqueueDocument(string json, string etag = "\"etag-1\"") =>
        Service.Responses.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            return Task.FromResult(response);
        });

    public void EnqueueStatus(HttpStatusCode status) =>
        Service.Responses.Enqueue(_ => Task.FromResult(new HttpResponseMessage(status)));

    public int RequestCount
    {
        get { lock (Service.Requests) return Service.Requests.Count; }
    }

    public HttpRequestMessage Request(int index)
    {
        lock (Service.Requests) return Service.Requests[index].Request;
    }

    public void Dispose() => Services.Dispose();

    public static string Hash(string html) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant();

    /// <summary>A <c>DocumentContent</c> body as served by <c>GET /v1/documents/{type}</c>.</summary>
    public static string DocumentJson(
        string type = "terms",
        int version = 2,
        string locale = "en",
        bool fallback = false,
        string title = "Terms of Service",
        string html = "<h1>Terms</h1><p>Version two.</p>",
        string effectiveFrom = "2026-09-01T00:00:00Z",
        string changeKind = "material",
        string? summary = "Clearer refund rules.") =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = type,
            ["name"] = title,
            ["requires_consent"] = type is "terms" or "dpa",
            ["revocable"] = false,
            ["version"] = version,
            ["status"] = "effective",
            ["change_kind"] = changeKind,
            ["effective_from"] = effectiveFrom,
            ["summary"] = summary,
            ["locale"] = locale,
            ["requested_locale"] = fallback ? "cs" : locale,
            ["fallback"] = fallback,
            ["title"] = title,
            ["content_hash"] = Hash(html),
            ["html"] = html,
            ["url"] = $"https://backoffice.test/l/p4pdf/{type}",
        });

    public static LegalDocument Manual(string slug = "terms-of-service", string locale = "en", int version = 1,
        string title = "Terms (manual)", string content = "# Manual terms", DateTimeOffset? effectiveFrom = null) => new()
    {
        Slug = slug,
        Locale = locale,
        Version = version,
        Title = title,
        Content = content,
        EffectiveFrom = effectiveFrom ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    public static LegalDocument Snapshot(string slug = "terms-of-service", string locale = "en", int version = 2,
        string title = "Terms (snapshot)", string html = "<p>Snapshot terms</p>") => new()
    {
        Slug = slug,
        Locale = locale,
        Version = version,
        Title = title,
        Content = "",
        ContentHtml = html,
        ContentHash = Hash(html),
        TypeKey = LegalSlugMap.TypeFor(slug),
        ChangeKind = "material",
        Source = LegalDocumentSources.Backoffice,
        EffectiveFrom = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        FetchedAt = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero),
    };
}
