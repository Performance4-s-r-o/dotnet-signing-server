using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetSigningServer.Tests.Services.Consents;

/// <summary>
/// The body of <c>POST /v1/consents</c> against the service's <c>ConsentBatch</c> /
/// <c>ConsentInput</c> schema (openapi.json), and delivery through the outbox — all against a
/// stubbed service.
/// </summary>
public class ConsentOutboxHandlerTests
{
    private static readonly string[] Required =
        ["subject_type", "subject_ref", "document", "version", "locale", "action", "occurred_at", "channel"];

    private static readonly HashSet<string> Allowed =
    [
        "subject_type", "subject_ref", "actor_ref", "document", "version", "locale", "content_hash", "purpose",
        "action", "occurred_at", "channel", "app_version", "ip", "user_agent", "metadata",
    ];

    [Fact]
    public async Task PostsTheBatchToV1Consents_InTheShapeOfConsentInput()
    {
        using var host = ConsentTestHost.Create("On");
        var user = await ConsentTestHost.SignUpAsync(host, "shape@example.com");
        host.Service.Enqueue(HttpStatusCode.Created, """{"data":[]}""");

        Assert.Equal(1, await host.Processor.DispatchDueAsync(CancellationToken.None));

        var (request, body) = Assert.Single(host.Service.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/consents", request.RequestUri!.AbsolutePath);
        var item = Assert.Single(await host.WithDbAsync(d => d.BackofficeOutboxItems.AsNoTracking().ToListAsync()));
        Assert.Equal(item.Id.ToString(), Assert.Single(request.Headers.GetValues("Idempotency-Key")));
        Assert.Equal(BackofficeOutboxStatus.Sent, item.Status);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(["events"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        var events = doc.RootElement.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(3, events.Count);
        foreach (var e in events)
        {
            var names = e.EnumerateObject().Select(p => p.Name).ToList();
            Assert.All(Required, r => Assert.Contains(r, names));
            Assert.All(names, n => Assert.Contains(n, Allowed));

            Assert.Equal("user", e.GetProperty("subject_type").GetString());
            Assert.Matches("^[A-Za-z0-9][A-Za-z0-9._:-]{2,199}$", e.GetProperty("subject_ref").GetString()!);
            Assert.Equal($"dotnet:user:{user.Id}", e.GetProperty("subject_ref").GetString());
            Assert.DoesNotContain("@", e.GetProperty("subject_ref").GetString());
            Assert.Matches("^[a-z][a-z0-9_]{1,39}$", e.GetProperty("document").GetString()!);
            Assert.Matches("^[a-z][a-z0-9_.-]{1,63}$", e.GetProperty("purpose").GetString()!);
            Assert.True(e.GetProperty("version").GetInt32() > 0);
            Assert.Matches("^[a-z]{2}(-[A-Z]{2})?$", e.GetProperty("locale").GetString()!);
            Assert.Contains(e.GetProperty("action").GetString(), new[] { "granted", "acknowledged" });
            Assert.Equal("web", e.GetProperty("channel").GetString());
            Assert.True(DateTimeOffset.TryParse(e.GetProperty("occurred_at").GetString(), out _));
            Assert.Equal("Mozilla/5.0 (test)", e.GetProperty("user_agent").GetString());
            Assert.Equal("signup", e.GetProperty("metadata").GetProperty("flow").GetString());

            // content_hash is a string when present — never null (the schema does not allow it).
            if (e.TryGetProperty("content_hash", out var hash))
            {
                Assert.Matches("^[0-9a-f]{64}$", hash.GetString()!);
            }

            var document = e.GetProperty("document").GetString();
            if (document == "dpa") Assert.Equal("203.0.113.7", e.GetProperty("ip").GetString());
            else Assert.Equal(JsonValueKind.Null, e.GetProperty("ip").ValueKind);
        }

        Assert.Equal(ConsentTestHost.TermsHash,
            events.Single(e => e.GetProperty("document").GetString() == "terms").GetProperty("content_hash").GetString());
        Assert.False(events.Single(e => e.GetProperty("document").GetString() == "dpa").TryGetProperty("content_hash", out _));
    }

    [Fact]
    public async Task ServiceDown_TenSignupsSucceed_ThenTenBatchesAreSentOnceEach()
    {
        using var host = ConsentTestHost.Create("On");
        for (var i = 0; i < 10; i++)
        {
            await ConsentTestHost.SignUpAsync(host, $"user{i}@example.com");
        }
        Assert.Equal(30, await host.WithDbAsync(d => d.ConsentRecords.CountAsync()));
        Assert.Equal(10, await host.WithDbAsync(d => d.BackofficeOutboxItems.CountAsync(i => i.Status == BackofficeOutboxStatus.Pending)));

        // Service down: every attempt fails, nothing is lost.
        for (var i = 0; i < 10; i++) host.Service.EnqueueProblem(HttpStatusCode.ServiceUnavailable, "unavailable");
        await host.Processor.DispatchDueAsync(CancellationToken.None);
        Assert.Equal(10, await host.WithDbAsync(d => d.BackofficeOutboxItems.CountAsync(i => i.Status == BackofficeOutboxStatus.Pending)));

        // Back up: each batch goes once.
        host.Time.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 10; i++) host.Service.Enqueue(HttpStatusCode.Created, """{"data":[]}""");
        await host.Processor.DispatchDueAsync(CancellationToken.None);
        host.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await host.Processor.DispatchDueAsync(CancellationToken.None));

        Assert.Equal(10, await host.WithDbAsync(d => d.BackofficeOutboxItems.CountAsync(i => i.Status == BackofficeOutboxStatus.Sent)));
        var delivered = host.Service.Requests.Skip(10).Select(r => r.Request.Headers.GetValues("Idempotency-Key").Single()).ToList();
        Assert.Equal(10, delivered.Count);
        Assert.Equal(10, delivered.Distinct().Count());
    }
}
