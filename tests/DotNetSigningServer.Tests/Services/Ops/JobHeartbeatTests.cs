using System.Net;
using DotNetSigningServer.Data;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Ops;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Ops;

/// <summary>#167: Healthchecks pings of the periodic jobs, grouped into shared checks.</summary>
public class JobHeartbeatTests
{
    private static readonly Uri Base = new("https://hc.example/ping/key123/");
    private const string P = "/ping/key123/";

    private static readonly HeartbeatJob A = new("job-a", TimeSpan.FromMinutes(1));
    private static readonly HeartbeatJob B = new("job-b", TimeSpan.FromMinutes(10));
    private static readonly HeartbeatJob Loose = new("job-loose", TimeSpan.FromMinutes(1));
    private static readonly HeartbeatGroup Group = new("group-ab", TimeSpan.FromMinutes(30), [A, B]);

    private sealed class Recorder : HttpMessageHandler
    {
        public List<(string Path, string Body)> Pings { get; } = new();
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? Answer { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Pings) Pings.Add((request.RequestUri!.AbsolutePath, body));
            return Answer is null ? new HttpResponseMessage(HttpStatusCode.OK) : await Answer(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private readonly Recorder _service = new();
    // 12:00:00 UTC: a cycle boundary of the 30 min group, so the process starts at the cycle's start.
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));

    private JobHeartbeat Create(params HeartbeatJob[] registered)
    {
        var sut = new JobHeartbeat(Base, new Factory(_service), _time, NullLogger<JobHeartbeat>.Instance, [Group]);
        foreach (var job in registered) sut.Register(job);
        return sut;
    }

    private string[] Paths() => _service.Pings.Select(p => p.Path).ToArray();

    private static Task Ok(JobHeartbeat sut, HeartbeatJob job) => sut.RunAsync(job, _ => Task.CompletedTask, CancellationToken.None);

    private static Task Fail(JobHeartbeat sut, HeartbeatJob job, Exception? ex = null) =>
        Assert.ThrowsAnyAsync<Exception>(() => sut.RunAsync(job, _ => Task.FromException(ex ?? new IOException("disk of jan@example.com")), CancellationToken.None));

    [Fact]
    public async Task Group_SucceedsOnlyAfterEveryJobSucceededInTheCycle()
    {
        var sut = Create(A, B);

        await Ok(sut, A);
        await Ok(sut, A);
        await sut.DrainAsync();
        Assert.Equal(new[] { P + "group-ab/start" }, Paths());

        await Ok(sut, B);
        await Ok(sut, A);
        await sut.DrainAsync();
        Assert.Equal(new[] { P + "group-ab/start", P + "group-ab" }, Paths());
        Assert.Equal("all ok: job-a, job-b", _service.Pings[1].Body);
    }

    [Fact]
    public async Task Failure_IsFailAtOnceWithJobAndTypeOnly_OncePerJobAndCycle()
    {
        var sut = Create(A, B);

        await Fail(sut, A);
        await Fail(sut, A);
        await Ok(sut, A);
        await Ok(sut, B);
        await sut.DrainAsync();

        Assert.Equal(new[] { P + "group-ab/start", P + "group-ab/fail" }, Paths());
        Assert.Equal("job-a: IOException", _service.Pings[1].Body);
        Assert.DoesNotContain("jan@", string.Concat(_service.Pings.Select(p => p.Body)));
    }

    [Fact]
    public async Task IncompleteCycle_FailsWhenTheNextOneBegins()
    {
        var sut = Create(A, B);

        await Ok(sut, A);
        _time.Advance(TimeSpan.FromMinutes(31));
        await Ok(sut, A);
        await sut.DrainAsync();

        Assert.Equal(new[] { P + "group-ab/start", P + "group-ab/fail", P + "group-ab/start" }, Paths());
        Assert.Equal("cycle 2026-11-04T12:00:00Z incomplete, missing: job-b", _service.Pings[1].Body);
    }

    [Fact]
    public async Task Restart_CycleBegunBeforeTheProcessIsNotJudged()
    {
        _time.Advance(TimeSpan.FromMinutes(10)); // the process starts mid-cycle
        var sut = Create(A, B);

        await Ok(sut, A);
        _time.Advance(TimeSpan.FromMinutes(25)); // next cycle, job-b never ran in the first one
        await Ok(sut, A);
        await Ok(sut, B);
        await sut.DrainAsync();

        Assert.Equal(new[] { P + "group-ab/start", P + "group-ab" }, Paths());
    }

    [Fact]
    public async Task Restart_PartialCycleStillSucceedsWhenAllRan()
    {
        _time.Advance(TimeSpan.FromMinutes(10));
        var sut = Create(A, B);

        await Ok(sut, A);
        await Ok(sut, B);
        await sut.DrainAsync();

        Assert.Equal(new[] { P + "group-ab" }, Paths());
    }

    [Fact]
    public async Task ModuleOff_JobNotRegistered_IsNotMissing()
    {
        var sut = Create(A); // job-b's module is off: never constructed

        await Ok(sut, A);
        _time.Advance(TimeSpan.FromMinutes(31));
        await Ok(sut, A);
        await sut.DrainAsync();

        Assert.Equal(new[] { P + "group-ab/start", P + "group-ab", P + "group-ab/start", P + "group-ab" }, Paths());
        Assert.Equal("all ok: job-a", _service.Pings[1].Body);
    }

    [Fact]
    public async Task FailureFinishedAfterTheCycle_IsStillReported_SuccessIsNot()
    {
        var sut = Create(A, B);
        var gate = new TaskCompletionSource();

        var slow = sut.RunAsync(B, async _ => { await gate.Task; throw new TimeoutException(); }, CancellationToken.None);
        await Ok(sut, A);
        _time.Advance(TimeSpan.FromMinutes(31));
        await Ok(sut, A); // the next cycle begins: the first is incomplete
        gate.SetResult();
        await Assert.ThrowsAsync<TimeoutException>(() => slow);
        await sut.DrainAsync();

        Assert.Equal(P + "group-ab/fail", _service.Pings.Last().Path);
        Assert.Equal("job-b: TimeoutException", _service.Pings.Last().Body);
    }

    [Fact]
    public async Task JobOutsideTheMap_GetsACheckOfItsOwnName()
    {
        var sut = Create(A, B);

        await Ok(sut, Loose);
        await Fail(sut, Loose, new InvalidOperationException("x", new TimeoutException()));
        await sut.DrainAsync();

        Assert.Equal(new[] { P + "job-loose/start", P + "job-loose", P + "job-loose/start", P + "job-loose/fail" }, Paths());
        Assert.Equal("job-loose: InvalidOperationException <- TimeoutException", _service.Pings[3].Body);
    }

    [Fact]
    public async Task Shutdown_IsNotAFailure()
    {
        var sut = Create(A, B);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.RunAsync(A, ct => Task.FromCanceled(ct), cts.Token));
        await sut.DrainAsync();

        Assert.DoesNotContain(Paths(), p => p.EndsWith("/fail"));
    }

    [Fact]
    public async Task Disabled_CallsNothing()
    {
        var ran = false;

        JobHeartbeat.Disabled.Register(HeartbeatJobs.PresignCleanup);
        await JobHeartbeat.Disabled.RunAsync(HeartbeatJobs.PresignCleanup, _ => { ran = true; return Task.CompletedTask; }, CancellationToken.None);
        var sut = new JobHeartbeat(null, new Factory(_service), _time, null, [Group]);
        await Ok(sut, A);
        await sut.DrainAsync();

        Assert.True(ran);
        Assert.Empty(_service.Pings);
    }

    [Fact]
    public async Task ServiceDown_DoesNotFailOrHoldTheJob()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        _service.Answer = _ => gate.Task; // hangs until the test lets it go
        var sut = Create(A);

        var run = sut.RunAsync(A, _ => Task.FromResult(1), CancellationToken.None);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(run, finished);
        Assert.Equal(1, await run);
        gate.SetException(new HttpRequestException("connection refused"));
        await sut.DrainAsync();
    }

    [Fact]
    public async Task ServiceError_IsSwallowed()
    {
        _service.Answer = _ => throw new HttpRequestException("refused");
        var sut = Create(A);

        Assert.Equal(7, await sut.RunAsync(A, _ => Task.FromResult(7), CancellationToken.None));
        await sut.DrainAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{{environment.DOTNET_HEARTBEAT_BASE_URL}}")]
    public void EmptyUrl_IsOff(string? raw) => Assert.Null(JobHeartbeat.ParseUrl(raw));

    [Theory]
    [InlineData("hc.example/ping")]
    [InlineData("ftp://hc.example/ping")]
    [InlineData("https://hc.example/ping?x=1")]
    public void InvalidUrl_StopsTheStart(string raw) =>
        Assert.Throws<InvalidOperationException>(() => JobHeartbeat.ParseUrl(raw));

    [Fact]
    public void Registration_WithoutUrl_IsDisabled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJobHeartbeats(new ConfigurationBuilder().Build());

        Assert.Same(JobHeartbeat.Disabled, services.BuildServiceProvider().GetRequiredService<IJobHeartbeat>());
    }

    [Fact]
    public void Map_EveryJobIsInExactlyOneCheck_AndTheMapIsValid()
    {
        Assert.Empty(HeartbeatJobs.Validate(HeartbeatJobs.Groups, HeartbeatJobs.All));
        foreach (var job in HeartbeatJobs.All)
        {
            Assert.Single(HeartbeatJobs.Groups, g => g.Jobs.Contains(job));
        }
        Assert.All(HeartbeatJobs.Groups.SelectMany(g => g.Jobs), j => Assert.Contains(j, HeartbeatJobs.All));
        Assert.Equal(4, HeartbeatJobs.Groups.Count); // the free Healthchecks tier is shared with the portal and the backoffice
        Assert.All(HeartbeatJobs.Groups, g => Assert.StartsWith("dotnet-", g.Name));
    }

    [Fact]
    public void Map_EveryJobRunsInEveryCycleOfItsGroup()
    {
        foreach (var group in HeartbeatJobs.Groups)
        {
            foreach (var job in group.Jobs)
            {
                // Room for drift (run time) and a retry after a failure: half an interval at least.
                Assert.True(job.Interval * 1.5 <= group.Cycle, $"{job.Name} runs every {job.Interval}, the {group.Name} cycle is {group.Cycle}");
            }
        }
    }

    [Fact]
    public void Map_IntervalsAreTheJobsOwn()
    {
        Assert.Equal(PresignCleanupService.CleanupInterval, HeartbeatJobs.PresignCleanup.Interval);
        Assert.Equal(DataRetentionService.Interval, HeartbeatJobs.DataRetention.Interval);
        Assert.True(P4BackofficeProductOptions.PollingOptions.DefaultInterval <= HeartbeatJobs.BackofficePolling.Interval);
        Assert.True(P4BackofficeProductOptions.PollingOptions.DefaultIntervalWithoutWebhooks <= HeartbeatJobs.BackofficePolling.Interval);
    }

    [Fact]
    public void Map_InvalidMapsAreReported()
    {
        var problems = HeartbeatJobs.Validate(
            [new HeartbeatGroup("job-a", TimeSpan.FromMinutes(5), [A]), new HeartbeatGroup("g2", TimeSpan.FromMinutes(5), [A]), new HeartbeatGroup("g3", TimeSpan.FromMinutes(5), [])],
            [A, B]);

        Assert.Contains(problems, p => p.Contains("has the name of a job"));
        Assert.Contains(problems, p => p.Contains("is in job-a and g2"));
        Assert.Contains(problems, p => p.Contains("g3 is empty"));
    }

    [Fact]
    public async Task PresignCleanup_RegistersAndPingsItsGroup()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var heartbeat = new JobHeartbeat(Base, new Factory(_service), _time, NullLogger<JobHeartbeat>.Instance);
        var worker = new PresignCleanupService(provider, NullLogger<PresignCleanupService>.Instance, heartbeat);

        await worker.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_service.Pings.Count < 1 && DateTime.UtcNow < deadline) await Task.Delay(20);
        await worker.StopAsync(CancellationToken.None);
        await heartbeat.DrainAsync();

        // Only presign-cleanup is registered here, so its run alone completes dotnet-maintenance.
        Assert.Equal(new[] { P + "dotnet-maintenance/start", P + "dotnet-maintenance" }, Paths());
    }
}
