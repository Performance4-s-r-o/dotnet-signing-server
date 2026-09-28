using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Backoffice.Email;

/// <summary>The raw SQL of the break-glass and paused-kind claims, on a real PostgreSQL.</summary>
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
    public async Task Fallback_ClaimsBlockedUnreachableAndOverdueCriticalItemsOnly()
    {
        using var email = await HostAsync();
        var overdue = await email.SendAsync(EmailTemplateId.TwoFactorCode, "a@example.com");
        var notCritical = await email.SendAsync(EmailTemplateId.PaymentFailed, "b@example.com");
        email.Host.Time.Advance(TimeSpan.FromSeconds(50));
        var fresh = await email.SendAsync(EmailTemplateId.PasswordReset, "c@example.com");
        var blocked = await email.SendAsync(EmailTemplateId.EmailVerification, "d@example.com");
        var unreachable = await email.SendAsync(EmailTemplateId.TwoFactorCode, "e@example.com");
        var failing = await email.SendAsync(EmailTemplateId.TwoFactorCode, "f@example.com");
        var timedOut = await email.SendAsync(EmailTemplateId.TwoFactorCode, "g@example.com");
        await email.UpdateAsync(blocked, i => i.Status = BackofficeOutboxStatus.Blocked);
        await email.UpdateAsync(unreachable, i => i.LastError = OutboxAttemptResult.NetworkErrorPrefix + "Connection refused");
        await email.UpdateAsync(failing, i => i.LastError = "HTTP 503");
        await email.UpdateAsync(timedOut, i => i.LastError = "Timeout");
        email.Host.Time.Advance(TimeSpan.FromSeconds(10)); // overdue is now 60 s old

        Assert.Equal(4, await email.Fallback.RunAsync(CancellationToken.None));

        Assert.Equal(
            new[] { "a@example.com", "d@example.com", "e@example.com", "f@example.com" },
            email.DirectSends.Select(s => s.To).OrderBy(t => t));
        foreach (var id in new[] { overdue, blocked, unreachable, failing })
        {
            var item = await email.Host.ItemAsync(id);
            Assert.Equal(BackofficeOutboxStatus.FallbackSent, item.Status);
            Assert.Null(item.PayloadProtected);
        }
        foreach (var id in new[] { notCritical, fresh, timedOut })
        {
            Assert.Equal(BackofficeOutboxStatus.Pending, (await email.Host.ItemAsync(id)).Status);
        }

        // Nothing is sent twice.
        Assert.Equal(0, await email.Fallback.RunAsync(CancellationToken.None));
    }

    [DockerFact]
    public async Task Fallback_ServiceDown_TakesEveryPendingCriticalItem_ButNotOldOnes()
    {
        using var email = await HostAsync();
        var old = await email.SendAsync(EmailTemplateId.TwoFactorCode, "old@example.com");
        email.Host.Time.Advance(BreakGlassEmailFallback.MaxAge + TimeSpan.FromMinutes(1));
        var fresh = await email.SendAsync(EmailTemplateId.TwoFactorCode, "new@example.com");
        for (var i = 0; i < OutboxCircuitBreaker.Threshold; i++) email.Breaker.RecordFailure();

        Assert.Equal(1, await email.Fallback.RunAsync(CancellationToken.None));

        Assert.Equal("new@example.com", Assert.Single(email.DirectSends).To);
        Assert.Equal(BackofficeOutboxStatus.FallbackSent, (await email.Host.ItemAsync(fresh)).Status);
        Assert.Equal(BackofficeOutboxStatus.Pending, (await email.Host.ItemAsync(old)).Status);
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
            email.Breaker,
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
