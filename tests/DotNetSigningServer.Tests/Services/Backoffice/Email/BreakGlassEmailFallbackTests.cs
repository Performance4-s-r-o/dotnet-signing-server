using System.Net;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Services.Backoffice.Email;

public class BreakGlassEmailFallbackTests
{
    private static readonly TimeSpan FallbackAfter = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task PendingCriticalItem_IsSentDirectlyOnlyAfterFallbackAfter()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.TwoFactorCode);

        email.Host.Time.Advance(FallbackAfter - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await email.Fallback.RunAsync(CancellationToken.None));
        Assert.Empty(email.DirectSends);

        email.Host.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await email.Fallback.RunAsync(CancellationToken.None));

        var (to, subject, html) = Assert.Single(email.DirectSends);
        Assert.Equal("jan@example.com", to);
        Assert.Equal("Subject two_factor_code", subject);
        Assert.Equal("<p>code 123456</p>", html);
        var item = await email.Host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.FallbackSent, item.Status);
        Assert.Null(item.PayloadProtected);
        Assert.Null(item.LockedUntil);
        Assert.Equal(email.Host.Time.Now, item.SentAt);
    }

    [Fact]
    public async Task ServiceDown_SendsRightAway()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.PasswordReset);
        for (var i = 0; i < OutboxCircuitBreaker.Threshold; i++) email.Breaker.RecordFailure();
        Assert.True(email.Breaker.IsServiceDown);

        Assert.Equal(1, await email.Fallback.RunAsync(CancellationToken.None));

        Assert.Single(email.DirectSends);
        Assert.Equal(BackofficeOutboxStatus.FallbackSent, (await email.Host.ItemAsync(id)).Status);
    }

    [Fact]
    public async Task ConnectionRefused_SendsOnTheNextPass()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.TwoFactorCode);
        email.Host.Service.Responses.Enqueue(_ => throw new HttpRequestException("Connection refused"));

        await email.Host.Processor.DispatchDueAsync(CancellationToken.None);
        Assert.False(email.Breaker.IsServiceDown); // one failure is not enough for the breaker…
        email.Host.Time.Advance(TimeSpan.FromSeconds(5));

        // …but this item's own attempt found the service unreachable.
        Assert.Equal(1, await email.Fallback.RunAsync(CancellationToken.None));
        Assert.Equal(BackofficeOutboxStatus.FallbackSent, (await email.Host.ItemAsync(id)).Status);
    }

    [Fact]
    public async Task Timeout_WaitsForFallbackAfter()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.TwoFactorCode);
        await email.UpdateAsync(id, i => { i.Attempts = 1; i.LastError = "Timeout"; });
        email.Host.Time.Advance(TimeSpan.FromSeconds(15));

        Assert.Equal(0, await email.Fallback.RunAsync(CancellationToken.None));

        email.Host.Time.Advance(FallbackAfter);
        Assert.Equal(1, await email.Fallback.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Blocked_IsSentRightAway()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.EmailVerification);
        email.Host.Service.EnqueueProblem(HttpStatusCode.Unauthorized, "unauthorized");
        await email.Host.Processor.DispatchDueAsync(CancellationToken.None);
        Assert.Equal(BackofficeOutboxStatus.Blocked, (await email.Host.ItemAsync(id)).Status);

        Assert.Equal(1, await email.Fallback.RunAsync(CancellationToken.None));

        Assert.Equal(BackofficeOutboxStatus.FallbackSent, (await email.Host.ItemAsync(id)).Status);
    }

    [Fact]
    public async Task FallbackSent_IsNeverSentAgain_NeitherDirectlyNorByTheService()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.TwoFactorCode);
        email.Host.Time.Advance(FallbackAfter);
        await email.Fallback.RunAsync(CancellationToken.None);

        email.Host.Time.Advance(FallbackAfter);
        Assert.Equal(0, await email.Fallback.RunAsync(CancellationToken.None));
        Assert.Equal(0, await email.Host.Processor.DispatchDueAsync(CancellationToken.None));
        await email.Host.WithDbAsync(db => OutboxHealth.RequeueBlockedAsync(db, email.Host.Time.Now));

        Assert.Single(email.DirectSends);
        Assert.Empty(email.Host.Service.Requests);
        Assert.Equal(BackofficeOutboxStatus.FallbackSent, (await email.Host.ItemAsync(id)).Status);
    }

    [Fact]
    public async Task NonCritical_IsNeverSentDirectly()
    {
        using var email = new EmailTestHost();
        await email.SendAsync(EmailTemplateId.AutoRechargeFailed);
        for (var i = 0; i < OutboxCircuitBreaker.Threshold; i++) email.Breaker.RecordFailure();
        email.Host.Time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, await email.Fallback.RunAsync(CancellationToken.None));
        Assert.Empty(email.DirectSends);
    }

    [Fact]
    public async Task SentByTheService_IsNotSentDirectly()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.TwoFactorCode);
        await email.Host.Processor.DispatchDueAsync(CancellationToken.None);
        email.Host.Time.Advance(FallbackAfter);

        Assert.Equal(0, await email.Fallback.RunAsync(CancellationToken.None));
        Assert.Equal(BackofficeOutboxStatus.Sent, (await email.Host.ItemAsync(id)).Status);
        Assert.Empty(email.DirectSends);
    }

    [Fact]
    public async Task ItemHeldByTheDispatcher_IsLeftAlone()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.TwoFactorCode);
        email.Host.Time.Advance(FallbackAfter);
        await email.UpdateAsync(id, i => i.LockedUntil = email.Host.Time.Now + TimeSpan.FromSeconds(30));

        Assert.Equal(0, await email.Fallback.RunAsync(CancellationToken.None));
        Assert.Empty(email.DirectSends);
    }

    [Fact]
    public async Task ResendFailure_KeepsTheItemPendingAndPausesIt()
    {
        using var email = new EmailTestHost();
        email.Resend.Setup(r => r.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("Resend failed: 500"));
        var id = await email.SendAsync(EmailTemplateId.TwoFactorCode);
        email.Host.Time.Advance(FallbackAfter);

        Assert.Equal(0, await email.Fallback.RunAsync(CancellationToken.None));

        var item = await email.Host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Pending, item.Status);
        Assert.NotNull(item.PayloadProtected);
        Assert.Equal(email.Host.Time.Now + BreakGlassEmailFallback.RetryPause, item.LockedUntil);
        Assert.Equal("Break-glass failed: InvalidOperationException", item.LastError);

        // Tried again once the pause is over.
        email.Resend.Setup(r => r.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        email.Host.Time.Advance(BreakGlassEmailFallback.RetryPause + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await email.Fallback.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Disabled_OrWithoutResend_DoesNothing()
    {
        using (var disabled = new EmailTestHost(fallbackDirect: false))
        {
            await disabled.SendAsync(EmailTemplateId.TwoFactorCode);
            disabled.Host.Time.Advance(FallbackAfter);
            Assert.Equal(0, await disabled.Fallback.RunAsync(CancellationToken.None));
            Assert.Empty(disabled.DirectSends);
        }

        using var unconfigured = new EmailTestHost(resendConfigured: false);
        await unconfigured.SendAsync(EmailTemplateId.TwoFactorCode);
        unconfigured.Host.Time.Advance(FallbackAfter);
        Assert.Equal(0, await unconfigured.Fallback.RunAsync(CancellationToken.None));
        Assert.Empty(unconfigured.DirectSends);
    }

    [Fact]
    public async Task OlderThanMaxAge_IsLeftAlone()
    {
        using var email = new EmailTestHost();
        await email.SendAsync(EmailTemplateId.TwoFactorCode);
        email.Host.Time.Advance(BreakGlassEmailFallback.MaxAge + TimeSpan.FromMinutes(1));

        Assert.Equal(0, await email.Fallback.RunAsync(CancellationToken.None));
        Assert.Empty(email.DirectSends);
    }

    [Fact]
    public async Task Dispatcher_RunsTheFallbackBeforeSending()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.TwoFactorCode);
        for (var i = 0; i < OutboxCircuitBreaker.Threshold; i++) email.Breaker.RecordFailure();
        var dispatcher = new BackofficeOutboxDispatcher(
            email.Host.Processor, email.Host.Signal, email.Host.Services.GetRequiredService<IServiceScopeFactory>(),
            email.Host.Time, NullLogger<BackofficeOutboxDispatcher>.Instance, [email.Fallback]);

        await dispatcher.RunPassAsync(CancellationToken.None);

        Assert.Equal(BackofficeOutboxStatus.FallbackSent, (await email.Host.ItemAsync(id)).Status);
        Assert.Empty(email.Host.Service.Requests);
    }
}
