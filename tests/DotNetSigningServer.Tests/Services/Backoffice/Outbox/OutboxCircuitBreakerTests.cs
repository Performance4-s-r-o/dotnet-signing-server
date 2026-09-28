using DotNetSigningServer.Services.Backoffice.Outbox;

namespace DotNetSigningServer.Tests.Services.Backoffice.Outbox;

public class OutboxCircuitBreakerTests
{
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void ThreeConsecutiveFailures_OpenForOneMinute()
    {
        var breaker = new OutboxCircuitBreaker(_time);
        breaker.Record(OutboxAttemptResult.NetworkError("refused"));
        breaker.Record(OutboxAttemptResult.Timeout());
        Assert.False(breaker.IsServiceDown);

        breaker.Record(OutboxAttemptResult.Response(503));
        Assert.True(breaker.IsServiceDown);

        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(breaker.IsServiceDown);
    }

    [Fact]
    public void FailuresSpreadOverMoreThanFiveMinutes_DoNotOpen()
    {
        var breaker = new OutboxCircuitBreaker(_time);
        breaker.Record(OutboxAttemptResult.Response(500));
        _time.Advance(TimeSpan.FromMinutes(3));
        breaker.Record(OutboxAttemptResult.Response(500));
        _time.Advance(TimeSpan.FromMinutes(3));
        breaker.Record(OutboxAttemptResult.Response(500));

        Assert.False(breaker.IsServiceDown);
    }

    [Fact]
    public void AnyAnswerBelow500_ResetsTheCount()
    {
        var breaker = new OutboxCircuitBreaker(_time);
        breaker.Record(OutboxAttemptResult.Response(500));
        breaker.Record(OutboxAttemptResult.Response(500));
        breaker.Record(OutboxAttemptResult.Response(422, "validation_failed"));
        breaker.Record(OutboxAttemptResult.Response(500));

        Assert.False(breaker.IsServiceDown);
    }

    [Fact]
    public void LocalFailures_AreIgnored()
    {
        var breaker = new OutboxCircuitBreaker(_time);
        for (var i = 0; i < 5; i++) breaker.Record(OutboxAttemptResult.LocalFailure("No handler"));

        Assert.False(breaker.IsServiceDown);
    }
}
