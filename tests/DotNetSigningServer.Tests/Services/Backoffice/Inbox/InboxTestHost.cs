using System.Net.Http.Headers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Tests.Services.Backoffice.Inbox;

/// <summary>InMemory (or given) database + inbox services wired as in the app, with a stubbed service.</summary>
internal sealed class InboxTestHost : IDisposable
{
    public const string Secret = "whsec_c2VjcmV0LWtleS0xMjM0NTY3ODkwMTIzNDU2";
    public const string PreviousSecret = "whsec_b2xkLXNlY3JldC1rZXktMDk4NzY1NDMyMQ==";

    public ServiceProvider Services { get; }
    public StubServiceHandler Service { get; } = new();
    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    public P4BackofficeProductOptions Options { get; }

    public InboxTestHost(
        Action<DbContextOptionsBuilder>? database = null,
        Action<P4BackofficeProductOptions>? configure = null,
        Action<IServiceCollection>? services = null)
    {
        Options = new P4BackofficeProductOptions
        {
            Mode = "On",
            BaseUrl = OutboxTestHost.BaseUrl,
            SecretKey = OutboxTestHost.SecretKey,
            Webhook = { Secret = Secret },
            DisabledReason = BackofficeDisabledReason.None,
        };
        configure?.Invoke(Options);

        var dbName = "inbox-" + Guid.NewGuid();
        var collection = new ServiceCollection();
        collection.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        collection.AddDbContext<ApplicationDbContext>(database ?? (o => o.UseInMemoryDatabase(dbName)));
        collection.AddSingleton<TimeProvider>(Time);
        collection.AddSingleton<IOptions<P4BackofficeProductOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
        collection.AddSingleton<BackofficeInboxSignal>();
        collection.AddScoped<BackofficeInbox>();
        collection.AddSingleton<LoggingBackofficeEventHandler>();
        collection.AddScoped<EventHandlerRegistry>();
        collection.AddSingleton<BackofficeInboxProcessor>();
        collection.AddSingleton<BackofficePollingService>();
        collection.AddHttpClient(BackofficePollingService.HttpClientName, c =>
            {
                c.BaseAddress = new Uri(OutboxTestHost.BaseUrl);
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", OutboxTestHost.SecretKey);
            })
            .ConfigurePrimaryHttpMessageHandler(() => Service);
        services?.Invoke(collection);
        Services = collection.BuildServiceProvider();
    }

    public BackofficeInboxProcessor Processor => Services.GetRequiredService<BackofficeInboxProcessor>();
    public BackofficePollingService Polling => Services.GetRequiredService<BackofficePollingService>();
    public BackofficeInboxSignal Signal => Services.GetRequiredService<BackofficeInboxSignal>();

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> action) =>
        WithScopeAsync(sp => action(sp.GetRequiredService<ApplicationDbContext>()));

    public Task<List<BackofficeWebhookInboxItem>> ItemsAsync() =>
        WithDbAsync(db => db.BackofficeWebhookInboxItems.AsNoTracking().OrderBy(i => i.ReceivedAt).ThenBy(i => i.WebhookId).ToListAsync());

    public Task<bool> AddAsync(string id, string type, string dataJson = "{}", string source = BackofficeInboxSource.Webhook) =>
        WithScopeAsync(sp => sp.GetRequiredService<BackofficeInbox>().AddIfNewAsync(id, type, dataJson, source));

    public Task<string?> CursorAsync() =>
        WithDbAsync(db => BackofficeStateStore.GetAsync(db, BackofficeStateKeys.EventsCursor));

    public void Dispose() => Services.Dispose();
}

/// <summary>Records every event it gets; can be told to fail.</summary>
internal sealed class RecordingEventHandler : IBackofficeEventHandler
{
    public RecordingEventHandler(params string[] types) => Types = types;

    public IReadOnlyCollection<string> Types { get; }

    public List<BackofficeEvent> Received { get; } = new();

    public Func<BackofficeEvent, Exception?> Fail { get; set; } = _ => null;

    public Task HandleAsync(BackofficeEvent evt, CancellationToken cancellationToken)
    {
        Received.Add(evt);
        var error = Fail(evt);
        return error is null ? Task.CompletedTask : Task.FromException(error);
    }
}
