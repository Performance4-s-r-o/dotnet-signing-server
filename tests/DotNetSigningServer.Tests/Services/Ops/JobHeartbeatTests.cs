using System.Net;
using DotNetSigningServer.Data;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Ops;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Ops;

/// <summary>#167: Healthchecks pings around the periodic jobs.</summary>
public class JobHeartbeatTests
{
    private static readonly Uri Base = new("https://hc.example/ping/key123/");

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
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));

    private JobHeartbeat Create(Uri? url = null) =>
        new(url ?? Base, new Factory(_service), _time, NullLogger<JobHeartbeat>.Instance);

    private string[] Paths() => _service.Pings.Select(p => p.Path).ToArray();

    [Fact]
    public async Task Success_PingsStartThenSuccessWithTheSummary()
    {
        var sut = Create();

        var result = await sut.RunAsync(HeartbeatJobs.PresignCleanup, _ => Task.FromResult(3), CancellationToken.None, n => $"removed {n}");
        await sut.DrainAsync();

        Assert.Equal(3, result);
        Assert.Equal(new[] { "/ping/key123/dotnet-presign-cleanup/start", "/ping/key123/dotnet-presign-cleanup" }, Paths());
        Assert.Equal("removed 3", _service.Pings[1].Body);
    }

    [Fact]
    public async Task Exception_PingsFailWithTheTypeOnly_AndIsRethrown()
    {
        var sut = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RunAsync(HeartbeatJobs.DataRetention,
            _ => Task.FromException(new InvalidOperationException("user jan@example.com", new TimeoutException("x"))), CancellationToken.None));
        await sut.DrainAsync();

        Assert.Equal(new[] { "/ping/key123/dotnet-data-retention/start", "/ping/key123/dotnet-data-retention/fail" }, Paths());
        Assert.Equal("InvalidOperationException <- TimeoutException", _service.Pings[1].Body);
        Assert.DoesNotContain("jan@", _service.Pings[1].Body);
    }

    [Fact]
    public async Task Shutdown_IsNotAFailure()
    {
        var sut = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.RunAsync(HeartbeatJobs.PresignCleanup,
            ct => Task.FromCanceled(ct), cts.Token));
        await sut.DrainAsync();

        Assert.DoesNotContain(Paths(), p => p.EndsWith("/fail"));
    }

    [Fact]
    public async Task Disabled_CallsNothing()
    {
        var ran = false;

        await JobHeartbeat.Disabled.RunAsync(HeartbeatJobs.PresignCleanup, _ => { ran = true; return Task.CompletedTask; }, CancellationToken.None);
        var sut = new JobHeartbeat(null, new Factory(_service), _time, null);
        await sut.RunAsync(HeartbeatJobs.PresignCleanup, _ => Task.CompletedTask, CancellationToken.None);
        await sut.DrainAsync();

        Assert.True(ran);
        Assert.Empty(_service.Pings);
    }

    [Fact]
    public async Task ServiceDown_DoesNotFailOrHoldTheJob()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        _service.Answer = _ => gate.Task; // hangs until the test lets it go
        var sut = Create();

        var run = sut.RunAsync(HeartbeatJobs.PresignCleanup, _ => Task.FromResult(1), CancellationToken.None);
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
        var sut = Create();

        var result = await sut.RunAsync(HeartbeatJobs.PresignCleanup, _ => Task.FromResult(7), CancellationToken.None);
        await sut.DrainAsync();

        Assert.Equal(7, result);
    }

    [Fact]
    public async Task FrequentLoop_PingsAtMostEveryMinute_ButAlwaysOnFailureAndRecovery()
    {
        var sut = Create();
        var job = HeartbeatJobs.BackofficeOutbox;

        await sut.RunAsync(job, _ => Task.CompletedTask, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(5));
        await sut.RunAsync(job, _ => Task.CompletedTask, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => sut.RunAsync(job, _ => Task.FromException(new IOException()), CancellationToken.None));
        _time.Advance(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => sut.RunAsync(job, _ => Task.FromException(new IOException()), CancellationToken.None));
        _time.Advance(TimeSpan.FromSeconds(5));
        await sut.RunAsync(job, _ => Task.CompletedTask, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(5));
        await sut.RunAsync(job, _ => Task.CompletedTask, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(1));
        await sut.RunAsync(job, _ => Task.CompletedTask, CancellationToken.None);
        await sut.DrainAsync();

        var p = "/ping/key123/dotnet-backoffice-outbox";
        Assert.Equal(new[] { p + "/start", p, p + "/fail", p, p + "/start", p }, Paths());
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
    public void CheckNames_AreUniqueAndPrefixed()
    {
        var names = HeartbeatJobs.All.Select(j => j.Name).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(names, n => Assert.Matches("^dotnet-[a-z-]+$", n));
    }

    [Fact]
    public async Task PresignCleanup_PingsAroundItsRun()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var heartbeat = Create();
        var worker = new PresignCleanupService(provider, NullLogger<PresignCleanupService>.Instance, heartbeat);

        await worker.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_service.Pings.Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(20);
        await worker.StopAsync(CancellationToken.None);
        await heartbeat.DrainAsync();

        Assert.Equal(new[] { "/ping/key123/dotnet-presign-cleanup/start", "/ping/key123/dotnet-presign-cleanup" }, Paths());
        Assert.Equal("removed 0", _service.Pings[1].Body);
    }
}
