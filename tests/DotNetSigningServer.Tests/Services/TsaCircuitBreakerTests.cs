using DotNetSigningServer.Services;

namespace DotNetSigningServer.Tests.Services;

/// <summary>#72: a failing authority is refused fast, probed once, and closed again on success.</summary>
[Collection("TsaStatics")]
public class TsaCircuitBreakerTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static TsaCircuitBreaker Breaker(FakeTime time, int threshold = 3, int openSeconds = 30) =>
        new(new TsaCircuitBreakerOptions { FailureThreshold = threshold, OpenSeconds = openSeconds }, time);

    [Fact]
    public void Opens_after_consecutive_failures_and_refuses_without_a_call()
    {
        var time = new FakeTime();
        var breaker = Breaker(time);

        for (var i = 0; i < 3; i++)
        {
            breaker.Acquire("tsa.example.com");
            breaker.RecordFailure("tsa.example.com");
        }

        var open = Assert.Throws<TsaCircuitOpenException>(() => breaker.Acquire("tsa.example.com"));
        Assert.Equal(TimeSpan.FromSeconds(30), open.RetryAfter);
        Assert.True(breaker.IsOpen("tsa.example.com"));
        // Another authority is unaffected.
        breaker.Acquire("other.example.com");
    }

    [Fact]
    public void A_success_in_between_resets_the_count()
    {
        var breaker = Breaker(new FakeTime());
        breaker.RecordFailure("h");
        breaker.RecordFailure("h");
        breaker.RecordSuccess("h");
        breaker.RecordFailure("h");
        breaker.RecordFailure("h");

        breaker.Acquire("h"); // still closed: never three in a row
    }

    [Fact]
    public void Half_open_lets_one_probe_through_and_closes_on_success()
    {
        var time = new FakeTime();
        var breaker = Breaker(time);
        for (var i = 0; i < 3; i++) breaker.RecordFailure("h");

        time.Now += TimeSpan.FromSeconds(31);
        breaker.Acquire("h"); // the probe
        Assert.Throws<TsaCircuitOpenException>(() => breaker.Acquire("h")); // everybody else waits for it

        breaker.RecordSuccess("h");
        breaker.Acquire("h");
        breaker.Acquire("h");
        Assert.False(breaker.IsOpen("h"));
    }

    [Fact]
    public void A_failed_probe_opens_it_again_for_a_full_period()
    {
        var time = new FakeTime();
        var breaker = Breaker(time);
        for (var i = 0; i < 3; i++) breaker.RecordFailure("h");

        time.Now += TimeSpan.FromSeconds(31);
        breaker.Acquire("h");
        breaker.RecordFailure("h");

        var open = Assert.Throws<TsaCircuitOpenException>(() => breaker.Acquire("h"));
        Assert.Equal(TimeSpan.FromSeconds(30), open.RetryAfter);
    }

    [Fact]
    public void Threshold_zero_turns_it_off()
    {
        var breaker = Breaker(new FakeTime(), threshold: 0);
        for (var i = 0; i < 50; i++) breaker.RecordFailure("h");
        breaker.Acquire("h");
    }

    [Theory]
    [InlineData(-1, 30)]
    [InlineData(101, 30)]
    [InlineData(5, 0)]
    [InlineData(5, 3601)]
    public void Invalid_settings_throw_at_construction(int threshold, int openSeconds)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new TsaCircuitBreaker(new TsaCircuitBreakerOptions { FailureThreshold = threshold, OpenSeconds = openSeconds }));
    }

    [Fact]
    public void The_shared_client_stops_calling_an_authority_that_keeps_failing()
    {
        var previous = TsaCircuitBreaker.Default;
        TsaCircuitBreaker.Default = Breaker(new FakeTime(), threshold: 2);
        try
        {
            // .invalid never resolves: each call is a transport failure, fast.
            var client = new PinnedIpTsaClient("https://tsa.breaker-test.invalid/tsr", null, null);
            var imprint = System.Security.Cryptography.SHA256.HashData(new byte[] { 1 });

            for (var i = 0; i < 2; i++) Assert.ThrowsAny<Exception>(() => client.GetTimeStampToken(imprint));

            var ex = Assert.ThrowsAny<Exception>(() => client.GetTimeStampToken(imprint));
            var chain = new List<Exception>();
            for (var e = ex; e != null; e = e.InnerException) chain.Add(e);
            Assert.Contains(chain, e => e is TsaCircuitOpenException);
        }
        finally
        {
            TsaCircuitBreaker.Default = previous;
        }
    }
}
