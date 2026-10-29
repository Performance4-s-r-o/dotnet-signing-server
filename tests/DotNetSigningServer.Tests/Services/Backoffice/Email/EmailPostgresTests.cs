using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Backoffice.Email;

/// <summary>The raw SQL of the paused-kind claim, on a real PostgreSQL.</summary>
[Trait("Category", "Db")]
public class EmailPostgresTests : IClassFixture<OutboxPostgresFixture>
{
    private readonly OutboxPostgresFixture _pg;

    public EmailPostgresTests(OutboxPostgresFixture pg)
    {
        _pg = pg;
    }

    private async Task<EmailTestHost> HostAsync()
    {
        await using (var db = _pg.CreateContext())
        {
            await db.BackofficeOutboxItems.ExecuteDeleteAsync();
        }
        return new EmailTestHost(database: _pg.Configure);
    }

    [DockerFact]
    public async Task PausedKinds_AreSkippedByTheClaim()
    {
        using var email = await HostAsync();
        var mail = await email.SendAsync(EmailTemplateId.PaymentFailed);
        var other = await email.Host.EnqueueAsync(new { n = 1 });
        var services = email.Host.Services;
        var processor = new OutboxProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetServices<IOutboxHandler>(),
            services.GetRequiredService<OutboxPayloadProtector>(),
            email.Host.Time,
            NullLogger<OutboxProcessor>.Instance,
            new OutboxKindFilter(["email."]));

        Assert.Equal(1, await processor.DispatchDueAsync(CancellationToken.None));

        Assert.Equal(BackofficeOutboxStatus.Sent, (await email.Host.ItemAsync(other)).Status);
        var item = await email.Host.ItemAsync(mail);
        Assert.Equal(BackofficeOutboxStatus.Pending, item.Status);
        Assert.Null(item.LockedUntil);
    }

    [DockerFact]
    public async Task Users_HaveLocaleAndEmailBouncedAt()
    {
        await using var db = _pg.CreateContext();
        var user = new User { Email = $"u{Guid.NewGuid():N}@example.com", Locale = "cs", EmailBouncedAt = DateTimeOffset.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        await using var check = _pg.CreateContext();
        var stored = await check.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Equal("cs", stored.Locale);
        Assert.NotNull(stored.EmailBouncedAt);
    }
}
