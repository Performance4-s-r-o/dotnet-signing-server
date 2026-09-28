using System.Net;
using System.Net.Http.Headers;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Consents;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services.Consents;

public class ConsentReconciliationServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly ConsentRequirement[] Requirements =
    [
        new("terms", ConsentActions.Granted),
        new("dpa", ConsentActions.Granted),
        new("privacy", ConsentActions.Acknowledged),
    ];

    private static LatestConsent[] Local(int version = 1) =>
    [
        new("terms", ConsentActions.Granted, version, T0),
        new("dpa", ConsentActions.Granted, version, T0),
        new("privacy", ConsentActions.Acknowledged, version, T0),
    ];

    private static RemoteDocumentConsent[] Remote(int version = 1) =>
    [
        new("terms", ConsentActions.Granted, version),
        new("dpa", ConsentActions.Granted, version),
        new("privacy", ConsentActions.Acknowledged, null),
    ];

    [Fact]
    public void Compare_Same_HasNoDifferences()
    {
        Assert.Empty(ConsentReconciliationService.Compare(Requirements, Local(), Remote()));
    }

    [Fact]
    public void Compare_OnlyLocal_IsMissingInTheService()
    {
        var differences = ConsentReconciliationService.Compare(Requirements, Local(), Remote().Where(r => r.Document != "dpa"));

        var difference = Assert.Single(differences);
        Assert.StartsWith("dpa: missing in the service", difference);
    }

    [Fact]
    public void Compare_OnlyRemote_IsOnlyInTheService()
    {
        var differences = ConsentReconciliationService.Compare(Requirements, Local().Where(l => l.Document != "terms"), Remote());

        var difference = Assert.Single(differences);
        Assert.Equal("terms: only in the service (granted)", difference);
    }

    [Fact]
    public void Compare_ActionMismatch_IsReported()
    {
        var remote = Remote();
        remote[0] = new("terms", ConsentActions.Revoked, null);

        var difference = Assert.Single(ConsentReconciliationService.Compare(Requirements, Local(), remote));

        Assert.Equal($"terms: action local granted, service {ConsentActions.Revoked}", difference);
    }

    [Fact]
    public void Compare_VersionMismatchOfAGrantedDocument_IsReported()
    {
        var remote = Remote();
        remote[1] = new("dpa", ConsentActions.Granted, 2);

        var difference = Assert.Single(ConsentReconciliationService.Compare(Requirements, Local(), remote));

        Assert.Equal("dpa: version local v1, service v2", difference);
    }

    [Fact]
    public void Compare_VersionOfAnAcknowledgedDocument_DoesNotMatter()
    {
        var local = Local();
        local[2] = new("privacy", ConsentActions.Acknowledged, 3, T0);

        Assert.Empty(ConsentReconciliationService.Compare(Requirements, local, Remote()));
    }

    [Fact]
    public void Compare_NeitherSide_HasNoDifferences()
    {
        Assert.Empty(ConsentReconciliationService.Compare(Requirements, [], []));
    }

    private const string RemoteAllV1 = """
        {"subject_ref":"x","documents":[
          {"document":"terms","last_action":"granted","granted_version":1},
          {"document":"dpa","last_action":"granted","granted_version":1},
          {"document":"privacy","last_action":"acknowledged","granted_version":null}]}
        """;

    private static OutboxTestHost Host(string mode, string dbName, StubServiceHandler status) =>
        new(o => o.UseInMemoryDatabase(dbName), services =>
        {
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(ConsentTestHost.Options(mode)));
            services.AddSingleton<IOutboxHandler, ConsentOutboxHandler>();
            services.AddScoped<ConsentService>();
            services.AddScoped<ConsentBackfill>();
            services.AddHttpClient(ConsentReconciliationService.HttpClientName, c =>
                {
                    c.BaseAddress = new Uri(OutboxTestHost.BaseUrl);
                    c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", OutboxTestHost.SecretKey);
                })
                .ConfigurePrimaryHttpMessageHandler(() => status);
            services.AddSingleton<ConsentReconciliationService>();
        });

    private static IEnumerable<string> StatusRequests(StubServiceHandler status) =>
        status.Requests
            .Select(r => r.Request.RequestUri!.AbsolutePath)
            .Where(p => p.EndsWith("/consent-status", StringComparison.Ordinal));

    [Fact]
    public async Task Run_ChecksUsersSentByTheBackfill()
    {
        var dbName = "consents-rec-" + Guid.NewGuid();
        User user;
        var status = new StubServiceHandler();
        using (var off = Host("Off", dbName, new StubServiceHandler()))
        {
            user = await ConsentTestHost.SignUpAsync(off, "backfilled@example.com");
        }

        using var on = Host("On", dbName, status);
        on.Time.Advance(TimeSpan.FromMinutes(5));
        using (var scope = on.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ConsentBackfill>().RunAsync();
        }
        // The backfill batch was delivered; the records still have no OutboxItemId.
        await on.WithDbAsync(async db =>
        {
            foreach (var item in await db.BackofficeOutboxItems.ToListAsync()) item.Status = BackofficeOutboxStatus.Sent;
            return await db.SaveChangesAsync();
        });
        Assert.All(await on.WithDbAsync(db => db.ConsentRecords.AsNoTracking().ToListAsync()), r => Assert.Null(r.OutboxItemId));
        status.Enqueue(HttpStatusCode.OK, RemoteAllV1);

        var differing = await on.Services.GetRequiredService<ConsentReconciliationService>().RunIfDueAsync(CancellationToken.None);

        Assert.Equal(0, differing);
        var request = Assert.Single(StatusRequests(status));
        Assert.Contains(user.Id.ToString("D"), request);
    }

    [Fact]
    public async Task Run_SkipsUsersWhoseBatchIsNotSentYet()
    {
        var dbName = "consents-rec-" + Guid.NewGuid();
        var status = new StubServiceHandler();
        using var on = Host("On", dbName, status);
        await ConsentTestHost.SignUpAsync(on, "pending@example.com");

        var differing = await on.Services.GetRequiredService<ConsentReconciliationService>().RunIfDueAsync(CancellationToken.None);

        Assert.Equal(0, differing);
        Assert.Empty(StatusRequests(status));
    }
}
