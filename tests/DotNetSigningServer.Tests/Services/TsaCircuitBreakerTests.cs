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
            // One exception type for "could not talk to the authority", whatever iText did around it.
            Assert.Contains(chain, e => e is TsaCommunicationException);
        }
        finally
        {
            TsaCircuitBreaker.Default = previous;
        }
    }

    [Fact]
    public void Only_transport_errors_timeouts_and_5xx_count_against_the_host()
    {
        Assert.False(TsaCircuitBreaker.CountsAsHostFailure(
            new HttpRequestException("401", null, System.Net.HttpStatusCode.Unauthorized)));
        Assert.False(TsaCircuitBreaker.CountsAsHostFailure(
            new HttpRequestException("404", null, System.Net.HttpStatusCode.NotFound)));
        Assert.True(TsaCircuitBreaker.CountsAsHostFailure(
            new HttpRequestException("503", null, System.Net.HttpStatusCode.ServiceUnavailable)));
        Assert.True(TsaCircuitBreaker.CountsAsHostFailure(new HttpRequestException("refused")));
        Assert.True(TsaCircuitBreaker.CountsAsHostFailure(new TaskCanceledException()));
        Assert.False(TsaCircuitBreaker.CountsAsHostFailure(new FormatException()));
    }

    [Fact]
    public void Failures_far_apart_are_not_in_a_row()
    {
        var time = new FakeTime();
        var breaker = Breaker(time, threshold: 3, openSeconds: 30);
        breaker.RecordFailure("h");
        breaker.RecordFailure("h");
        time.Now += TimeSpan.FromMinutes(10);
        breaker.RecordFailure("h");

        breaker.Acquire("h"); // the count restarted: one failure, not three
    }

    [Fact]
    public void Probe_scope_does_not_feed_the_breaker()
    {
        var previous = TsaCircuitBreaker.Default;
        TsaCircuitBreaker.Default = Breaker(new FakeTime(), threshold: 2);
        try
        {
            var client = new PinnedIpTsaClient("https://tsa.probe-test.invalid/tsr", null, null);
            var imprint = System.Security.Cryptography.SHA256.HashData(new byte[] { 1 });

            using (TsaCircuitBreaker.Bypass())
            {
                for (var i = 0; i < 5; i++) Assert.ThrowsAny<Exception>(() => client.GetTimeStampToken(imprint));
            }

            Assert.False(TsaCircuitBreaker.Default.IsOpen(client.BreakerKey));
        }
        finally
        {
            TsaCircuitBreaker.Default = previous;
        }
    }

    [Fact]
    public void Credentials_are_part_of_the_key_so_one_wrong_password_is_not_everybodys_outage()
    {
        var a = new PinnedIpTsaClient("https://tsa.example.com/tsr", "tenant-a", "wrong");
        var b = new PinnedIpTsaClient("https://tsa.example.com/tsr", "tenant-b", "right");
        var a2 = new PinnedIpTsaClient("https://tsa.example.com/tsr", "tenant-a", "wrong");

        Assert.NotEqual(a.BreakerKey, b.BreakerKey);
        Assert.Equal(a.BreakerKey, a2.BreakerKey);
        // Same host on another port is another authority.
        Assert.NotEqual(new PinnedIpTsaClient("https://tsa.example/tsr", null, null).BreakerKey,
            new PinnedIpTsaClient("https://tsa.example:8443/tsr", null, null).BreakerKey);
        Assert.DoesNotContain("wrong", a.BreakerKey);
    }

    [Fact]
    public void The_host_map_stays_bounded_under_a_flood_of_made_up_hosts()
    {
        var breaker = Breaker(new FakeTime(), threshold: 1);
        for (var i = 0; i < 3000; i++) breaker.RecordFailure($"h{i}.example.com");

        Assert.True(breaker.TrackedHosts <= 1024, $"tracked {breaker.TrackedHosts}");
    }

    [Fact]
    public async Task Abandoned_work_that_reads_a_disposed_deadline_does_not_count_against_the_authority()
    {
        var previous = TsaCircuitBreaker.Default;
        TsaCircuitBreaker.Default = Breaker(new FakeTime(), threshold: 1);
        try
        {
            var client = new PinnedIpTsaClient("https://tsa.abandoned-test.invalid/tsr", null, null);
            var imprint = System.Security.Cryptography.SHA256.HashData(new byte[] { 1 });
            var deadline = new DotNetSigningServer.Services.Operations.OperationDeadline(
                TimeSpan.FromMinutes(1), CancellationToken.None);

            // The attempt was abandoned: its token was cancelled and the request is long gone.
            deadline.Cancel();
            deadline.Dispose();
            var work = deadline.Run(_ =>
            {
                Assert.ThrowsAny<Exception>(() => client.GetTimeStampToken(imprint));
                return Task.FromResult(0);
            });
            await work;

            Assert.False(TsaCircuitBreaker.Default.IsOpen(client.BreakerKey),
                "our own cancellation must not be recorded as the authority failing");
        }
        finally
        {
            TsaCircuitBreaker.Default = previous;
        }
    }
}
