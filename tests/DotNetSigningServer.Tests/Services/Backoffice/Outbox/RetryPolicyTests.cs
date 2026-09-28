using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Outbox;

namespace DotNetSigningServer.Tests.Services.Backoffice.Outbox;

public class RetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static BackofficeOutboxItem Item(int attempts = 0) => new()
    {
        Kind = "test",
        PayloadProtected = "protected",
        Attempts = attempts,
        CreatedAt = Now.AddHours(-1),
        NextAttemptAt = Now,
        LockedUntil = Now.AddMinutes(2),
    };

    [Theory]
    // status, problem code, attempts before, expected status, expected delay (seconds; -1 = no next attempt)
    [InlineData(202, null, 0, BackofficeOutboxStatus.Sent, -1)]
    [InlineData(200, null, 5, BackofficeOutboxStatus.Sent, -1)]
    [InlineData(503, null, 0, BackofficeOutboxStatus.Pending, 5)]
    [InlineData(500, null, 1, BackofficeOutboxStatus.Pending, 30)]
    [InlineData(502, null, 2, BackofficeOutboxStatus.Pending, 120)]
    [InlineData(429, null, 3, BackofficeOutboxStatus.Pending, 600)]
    [InlineData(408, null, 4, BackofficeOutboxStatus.Pending, 1800)]
    [InlineData(504, null, 5, BackofficeOutboxStatus.Pending, 3600)]
    [InlineData(503, null, 6, BackofficeOutboxStatus.Pending, 21600)]
    [InlineData(503, null, 16, BackofficeOutboxStatus.Pending, 21600)]
    [InlineData(503, null, 17, BackofficeOutboxStatus.Dead, -1)]
    [InlineData(401, null, 0, BackofficeOutboxStatus.Blocked, -1)]
    [InlineData(403, "insufficient_scope", 2, BackofficeOutboxStatus.Blocked, -1)]
    [InlineData(400, "validation_failed", 0, BackofficeOutboxStatus.Dead, -1)]
    [InlineData(404, null, 0, BackofficeOutboxStatus.Dead, -1)]
    [InlineData(422, "consent_invalid", 0, BackofficeOutboxStatus.Dead, -1)]
    [InlineData(422, "idempotency_key_reused", 0, BackofficeOutboxStatus.Dead, -1)]
    [InlineData(422, "suppressed_recipient", 0, BackofficeOutboxStatus.Dead, -1)]
    [InlineData(409, "idempotency_replayed", 1, BackofficeOutboxStatus.Sent, -1)]
    [InlineData(409, "idempotency_in_progress", 0, BackofficeOutboxStatus.Pending, 5)]
    [InlineData(409, "support_not_configured", 0, BackofficeOutboxStatus.Dead, -1)]
    public void Apply_StatusAndAttempt_GiveStateAndNextAttempt(
        int status, string? code, int attemptsBefore, string expectedStatus, int expectedDelaySeconds)
    {
        var item = Item(attemptsBefore);

        RetryPolicy.Apply(item, OutboxAttemptResult.Response(status, code, remoteId: "r_1"), Now);

        Assert.Equal(expectedStatus, item.Status);
        Assert.Equal(attemptsBefore + 1, item.Attempts);
        Assert.Null(item.LockedUntil);
        if (expectedDelaySeconds >= 0)
        {
            Assert.Equal(Now.AddSeconds(expectedDelaySeconds), item.NextAttemptAt);
        }
    }

    [Fact]
    public void Sent_StoresRemoteIdAndClearsPayloadAndError()
    {
        var item = Item(2);
        item.LastError = "HTTP 503";

        var outcome = RetryPolicy.Apply(item, OutboxAttemptResult.Response(202, remoteId: "em_123"), Now);

        Assert.Equal(OutboxOutcome.Sent, outcome);
        Assert.Equal("em_123", item.RemoteId);
        Assert.Null(item.PayloadProtected);
        Assert.Null(item.LastError);
        Assert.Equal(Now, item.SentAt);
    }

    [Fact]
    public void Blocked_AndDead_KeepThePayload()
    {
        var blocked = Item();
        var dead = Item();

        RetryPolicy.Apply(blocked, OutboxAttemptResult.Response(401), Now);
        RetryPolicy.Apply(dead, OutboxAttemptResult.Response(400, "validation_failed"), Now);

        Assert.NotNull(blocked.PayloadProtected);
        Assert.NotNull(dead.PayloadProtected);
        Assert.Equal("HTTP 400 validation_failed", dead.LastError);
    }

    [Fact]
    public void Suppressed_IsDeadWithoutBeingAnError()
    {
        Assert.Equal(OutboxOutcome.Suppressed, RetryPolicy.Classify(OutboxAttemptResult.Response(422, "suppressed_recipient")));
        Assert.Equal(OutboxOutcome.Dead, RetryPolicy.Classify(OutboxAttemptResult.Response(422, "validation_failed")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NetworkErrorAndTimeout_AreRetried(bool timeout)
    {
        var item = Item();
        var result = timeout ? OutboxAttemptResult.Timeout() : OutboxAttemptResult.NetworkError("connection refused");

        var outcome = RetryPolicy.Apply(item, result, Now);

        Assert.Equal(OutboxOutcome.Retry, outcome);
        Assert.Equal(BackofficeOutboxStatus.Pending, item.Status);
        Assert.Equal(Now.AddSeconds(5), item.NextAttemptAt);
        Assert.True(RetryPolicy.IsServiceFailure(result));
    }

    [Fact]
    public void RetryAfter_ExtendsTheDelayButNeverBeyondSixHours()
    {
        var item = Item();
        RetryPolicy.Apply(item, OutboxAttemptResult.Response(429, retryAfter: TimeSpan.FromMinutes(1)), Now);
        Assert.Equal(Now.AddMinutes(1), item.NextAttemptAt);

        var capped = Item();
        RetryPolicy.Apply(capped, OutboxAttemptResult.Response(429, retryAfter: TimeSpan.FromDays(2)), Now);
        Assert.Equal(Now.AddHours(6), capped.NextAttemptAt);
    }

    [Fact]
    public void Schedule_GivesUpOnceSeventyTwoHoursAreSpent()
    {
        Assert.True(RetryPolicy.ElapsedBefore(16) <= RetryPolicy.GiveUpAfter);
        Assert.True(RetryPolicy.ElapsedBefore(17) <= RetryPolicy.GiveUpAfter);
        Assert.True(RetryPolicy.ElapsedBefore(18) > RetryPolicy.GiveUpAfter);
    }

    [Fact]
    public void LocalFailures_AreRetriedButDoNotCountAgainstTheService()
    {
        var noHandler = OutboxAttemptResult.LocalFailure("No handler for kind 'x'");
        Assert.Equal(OutboxOutcome.Retry, RetryPolicy.Classify(noHandler));
        Assert.False(RetryPolicy.IsServiceFailure(noHandler));

        Assert.Equal(OutboxOutcome.Dead, RetryPolicy.Classify(OutboxAttemptResult.Fatal("Payload cannot be decrypted")));
    }

    [Fact]
    public void LongErrorsAndRemoteIds_AreTruncatedToTheColumns()
    {
        var item = Item();
        RetryPolicy.Apply(item, OutboxAttemptResult.NetworkError(new string('x', 2000)), Now);
        Assert.Equal(RetryPolicy.MaxErrorLength, item.LastError!.Length);

        var sent = Item();
        RetryPolicy.Apply(sent, OutboxAttemptResult.Response(201, remoteId: new string('y', 100)), Now);
        Assert.Equal(RetryPolicy.MaxRemoteIdLength, sent.RemoteId!.Length);
    }
}
