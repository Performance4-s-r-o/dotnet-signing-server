using System.Net;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Support;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Support;

/// <summary>The On path against PostgreSQL with every migration applied (Testcontainers).</summary>
[Trait("Category", "Db")]
public class SupportPostgresTests : IClassFixture<OutboxPostgresFixture>
{
    private readonly OutboxPostgresFixture _pg;

    public SupportPostgresTests(OutboxPostgresFixture pg)
    {
        _pg = pg;
    }

    [DockerFact]
    public async Task On_ServiceDownThenUp_TicketIsCreatedOnce()
    {
        using var host = new OutboxTestHost(_pg.Configure, s =>
            s.AddSingleton<IOutboxHandler>(new SupportTicketOutboxHandler(NullLogger<SupportTicketOutboxHandler>.Instance)));
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = TestHelpers.CreateTestUser($"pg-{Guid.NewGuid():N}@example.com");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var formKey = Guid.NewGuid().ToString("N");

        host.Service.Enqueue(HttpStatusCode.ServiceUnavailable);
        var first = SupportControllerTests.Create(host, scope, user, new SupportControllerTests.OsTicketStub(), new SupportCategoriesHolder(), "On");
        await first.SubmitTicket("s", "m", "other", "normal", formKey);
        Assert.Equal("SupportTicketQueued", first.TempData["Info"]);

        // The service is back: the dispatcher delivers the queued ticket.
        host.Service.Enqueue(HttpStatusCode.Created, """{"id":"5b1c","status":"submitted","ticket_number":"31","portal_url":null}""");
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await host.Processor.DispatchDueAsync(CancellationToken.None);

        // The same form submitted again: no second ticket, the number is shown.
        var again = SupportControllerTests.Create(host, scope, user, new SupportControllerTests.OsTicketStub(), new SupportCategoriesHolder(), "On");
        await again.SubmitTicket("s", "m", "other", "normal", formKey);
        Assert.Equal("SupportTicketCreatedWithId".Replace("{0}", "31"), again.TempData["Info"]);

        var items = await host.WithDbAsync(d => d.BackofficeOutboxItems.AsNoTracking()
            .Where(i => i.SubjectRef!.StartsWith($"dotnet:user:{user.Id:D}/")).ToListAsync());
        var item = Assert.Single(items);
        Assert.Equal(BackofficeOutboxStatus.Sent, item.Status);
        Assert.Equal("31", SupportTicketReceipt.TicketNumber(item.RemoteId));
        Assert.Equal(2, host.Service.Requests.Count);
        Assert.All(host.Service.Requests, r =>
            Assert.Equal(item.Id.ToString(), r.Request.Headers.GetValues(OutboxRequest.IdempotencyKeyHeader).Single()));
    }
}
