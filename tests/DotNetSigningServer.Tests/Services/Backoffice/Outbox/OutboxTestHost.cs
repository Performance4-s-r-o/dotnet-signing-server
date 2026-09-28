using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotNetSigningServer.Data;
using DotNetSigningServer.Services.Backoffice.Outbox;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Backoffice.Outbox;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Stands in for the service: records requests, answers from a queue (default 202).</summary>
internal sealed class StubServiceHandler : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

    public Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> Responses { get; } = new();

    public void Enqueue(HttpStatusCode status, string? json = null, TimeSpan? retryAfter = null) =>
        Responses.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status);
            if (json != null) response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (retryAfter != null) response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
            return Task.FromResult(response);
        });

    public void EnqueueProblem(HttpStatusCode status, string code) =>
        Enqueue(status, $$"""{"type":"about:blank","title":"x","status":{{(int)status}},"code":"{{code}}"}""");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests) Requests.Add((request, body));
        var next = Responses.Count > 0
            ? Responses.Dequeue()
            : _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"id":"res_1","status":"queued"}""", Encoding.UTF8, "application/json"),
            });
        return await next(request);
    }
}

/// <summary>POSTs <c>test</c> items to <c>v1/test</c>.</summary>
internal sealed class TestOutboxHandler : JsonPostOutboxHandler
{
    public const string TestKind = "test";
    public override string Kind => TestKind;
    protected override string Path => "v1/test";
}

/// <summary>InMemory database + outbox services wired as in the app, with a stubbed service.</summary>
internal sealed class OutboxTestHost : IDisposable
{
    public const string BaseUrl = "https://backoffice.test/";
    public const string SecretKey = "p4sk_test_secret";

    public ServiceProvider Services { get; }
    public StubServiceHandler Service { get; } = new();
    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));

    /// <param name="database">Database to use instead of a fresh InMemory one.</param>
    /// <param name="configure">Extra registrations (other handlers, module services).</param>
    public OutboxTestHost(Action<DbContextOptionsBuilder>? database = null, Action<IServiceCollection>? configure = null)
    {
        var dbName = "outbox-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<ApplicationDbContext>(database ?? (o => o.UseInMemoryDatabase(dbName)));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<OutboxSignal>();
        services.AddSingleton<OutboxPayloadProtector>();
        services.AddSingleton<OutboxCircuitBreaker>();
        services.AddScoped<IBackofficeOutbox, BackofficeOutbox>();
        services.AddSingleton<IOutboxHandler, TestOutboxHandler>();
        services.AddSingleton<OutboxProcessor>();
        services.AddHttpClient(OutboxProcessor.HttpClientName, c =>
            {
                c.BaseAddress = new Uri(BaseUrl);
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SecretKey);
            })
            .ConfigurePrimaryHttpMessageHandler(() => Service);
        configure?.Invoke(services);
        Services = services.BuildServiceProvider();
    }

    public OutboxProcessor Processor => Services.GetRequiredService<OutboxProcessor>();
    public OutboxSignal Signal => Services.GetRequiredService<OutboxSignal>();

    /// <summary>Enqueues and saves in one scope, like a request would.</summary>
    public async Task<Guid> EnqueueAsync(object payload, bool critical = false, string kind = TestOutboxHandler.TestKind)
    {
        using var scope = Services.CreateScope();
        var id = scope.ServiceProvider.GetRequiredService<IBackofficeOutbox>().Enqueue(kind, payload, critical, "user:test");
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().SaveChangesAsync();
        return id;
    }

    public async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    public Task<DotNetSigningServer.Models.BackofficeOutboxItem> ItemAsync(Guid id) =>
        WithDbAsync(db => db.BackofficeOutboxItems.AsNoTracking().SingleAsync(i => i.Id == id));

    public void Dispose() => Services.Dispose();
}

internal static class NullLoggers
{
    public static ILogger<T> For<T>() => NullLogger<T>.Instance;
}
