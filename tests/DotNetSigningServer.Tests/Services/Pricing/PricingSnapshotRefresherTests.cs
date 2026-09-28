using System.Net;
using System.Text;
using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Stripe;

namespace DotNetSigningServer.Tests.Services.Pricing;

/// <summary>
/// InMemory database + the Pricing module wired by <see cref="BackofficeRegistration.AddPricing"/>,
/// with the service replaced by <see cref="StubServiceHandler"/> and Stripe by a mock.
/// </summary>
internal sealed class PricingTestHost : IDisposable
{
    public ServiceProvider Services { get; }
    public StubServiceHandler Service { get; } = new();
    public Mock<IStripeClient> Stripe { get; } = new();
    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    public int StripeLookups { get; private set; }

    /// <summary>Stands in for e-mail (price-change notices).</summary>
    public FakeTemplatedEmailSender Email { get; } = new();

    public PricingTestHost(string mode, BillingOptions? billing = null)
    {
        var options = new P4BackofficeProductOptions
        {
            Modules = { Pricing = mode },
            BaseUrl = OutboxTestHost.BaseUrl,
            SecretKey = OutboxTestHost.SecretKey,
            DisabledReason = BackofficeDisabledReason.None,
        };

        Stripe.Setup(c => c.RequestAsync<StripeList<Price>>(
                HttpMethod.Get, It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => StripeLookups++)
            .ReturnsAsync(new StripeList<Price> { Data = [] });

        var dbName = "pricing-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<TimeProvider>(Time);
        services.Configure<BillingOptions>(o =>
        {
            var b = billing ?? new BillingOptions();
            o.PricePer100 = b.PricePer100;
            o.Currency = b.Currency;
        });
        services.Configure<StripeOptions>(_ => { });
        services.Configure<AppOptions>(o => o.FqdnServerName = "https://app.example.com");
        services.AddSingleton<DotNetSigningServer.Services.Email.ITemplatedEmailSender>(Email);
        services.AddSingleton(sp => new StripePriceResolver(Stripe.Object, Time, sp.GetRequiredService<ILogger<StripePriceResolver>>()));
        BackofficeRegistration.AddPricing(services, options);
        services.AddSingleton<LoggingBackofficeEventHandler>();
        services.AddScoped<EventHandlerRegistry>();
        services.AddHttpClient(BackofficePricingClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => Service);
        Services = services.BuildServiceProvider();
    }

    public PricingSnapshotRefresher Refresher => Services.GetRequiredService<PricingSnapshotRefresher>();
    public PricingSnapshotHolder Holder => Services.GetRequiredService<PricingSnapshotHolder>();
    public ICreditPricingProvider Provider => Services.GetRequiredService<ICreditPricingProvider>();
    public StripePriceResolver Resolver => Services.GetRequiredService<StripePriceResolver>();

    public void EnqueuePriceList(string json, string etag = "\"v1\"") =>
        Service.Responses.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            return Task.FromResult(response);
        });

    public void EnqueueStatus(HttpStatusCode status) =>
        Service.Responses.Enqueue(_ => Task.FromResult(new HttpResponseMessage(status)));

    public HttpRequestMessage Request(int index)
    {
        lock (Service.Requests) return Service.Requests[index].Request;
    }

    public int RequestCount
    {
        get { lock (Service.Requests) return Service.Requests.Count; }
    }

    public async Task<string?> StoredAsync()
    {
        using var scope = Services.CreateScope();
        return await BackofficeStateStore.GetAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), BackofficeStateKeys.PricingCurrent);
    }

    public async Task StoreAsync(string value)
    {
        using var scope = Services.CreateScope();
        await BackofficeStateStore.SetAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            BackofficeStateKeys.PricingCurrent, value, Time.GetUtcNow());
    }

    public async Task<User> AddUserAsync(Action<User> configure)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = new User { Email = $"u{Guid.NewGuid():N}@example.com" };
        configure(user);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task<User> UserAsync(Guid id)
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == id);
    }

    /// <summary>Runs the registered handler of <paramref name="type"/> in its own scope.</summary>
    public async Task HandleAsync(string type, string dataJson, string id = "msg_1", string source = "webhook")
    {
        using var scope = Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<EventHandlerRegistry>();
        var handler = registry.Find(type) ?? throw new InvalidOperationException("no handler for " + type);
        using var data = JsonDocument.Parse(dataJson);
        await handler.HandleAsync(new BackofficeEvent(id, type, data.RootElement.Clone(), source, Time.GetUtcNow(), 1), CancellationToken.None);
    }

    public void Dispose() => Services.Dispose();
}

public class PricingSnapshotRefresherTests
{
    [Fact]
    public async Task Refresh_StoresThePriceListAndServesIt()
    {
        using var host = new PricingTestHost("On");
        host.EnqueuePriceList(PricingTestData.ImportedJson(4, new Dictionary<int, long> { [300] = 1500 }), "\"v4\"");

        var outcome = await host.Refresher.RefreshAsync("test", conditional: true, CancellationToken.None);

        Assert.Equal(PricingRefreshOutcome.Updated, outcome);
        Assert.Equal("v1/pricing/current", host.Request(0).RequestUri!.PathAndQuery.TrimStart('/'));
        Assert.Equal(4, host.Holder.Current!.Version);
        Assert.Equal(1500, host.Provider.GetPack(300)!.UnitAmountMinor);
        Assert.Equal(CreditPricing.SourceBackoffice, host.Provider.Source);
        var stored = PricingSnapshot.TryDeserialize(await host.StoredAsync());
        Assert.Equal("\"v4\"", stored!.ETag);
        Assert.Equal(4, stored.Version);
    }

    [Fact]
    public async Task SecondRefresh_IsConditional()
    {
        using var host = new PricingTestHost("On");
        host.EnqueuePriceList(PricingTestData.ImportedJson(1), "\"v1\"");
        await host.Refresher.RefreshAsync("test", conditional: true, CancellationToken.None);
        host.EnqueueStatus(HttpStatusCode.NotModified);

        var outcome = await host.Refresher.RefreshAsync("test", conditional: true, CancellationToken.None);

        Assert.Equal(PricingRefreshOutcome.NotModified, outcome);
        Assert.Equal("\"v1\"", host.Request(1).Headers.IfNoneMatch.Single().ToString());
        Assert.Equal(1, host.Holder.Current!.Version);
    }

    [Fact]
    public async Task ServiceDown_KeepsTheSnapshotAndThrows()
    {
        using var host = new PricingTestHost("On");
        host.EnqueuePriceList(PricingTestData.ImportedJson(1, new Dictionary<int, long> { [100] = 600 }));
        await host.Refresher.RefreshAsync("test", conditional: true, CancellationToken.None);
        host.EnqueueStatus(HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<HttpRequestException>(() => host.Refresher.RefreshAsync("test", conditional: true, CancellationToken.None));

        Assert.Equal(600, host.Provider.GetPack(100)!.UnitAmountMinor);
    }

    [Fact]
    public async Task InvalidBody_IsRejected()
    {
        using var host = new PricingTestHost("On");
        host.EnqueuePriceList("""{"version":1}""");

        await Assert.ThrowsAsync<HttpRequestException>(() => host.Refresher.RefreshAsync("test", conditional: true, CancellationToken.None));

        Assert.Null(host.Holder.Current);
        Assert.Null(await host.StoredAsync());
    }

    [Fact]
    public async Task StoredSnapshot_IsServedWithoutTheService()
    {
        using var host = new PricingTestHost("On");
        await host.StoreAsync(PricingTestData.Snapshot(PricingTestData.ImportedJson(2, new Dictionary<int, long> { [1000] = 4000 })).Serialize());

        Assert.True(await host.Refresher.LoadStoredAsync(CancellationToken.None));

        Assert.Equal(4000, host.Provider.GetPack(1000)!.UnitAmountMinor);
        Assert.Equal(0, host.RequestCount);
    }

    [Fact]
    public async Task WithoutSnapshot_OnServesTheConfiguredPrices()
    {
        using var host = new PricingTestHost("On", new BillingOptions { PricePer100 = 5m });

        Assert.False(await host.Refresher.LoadStoredAsync(CancellationToken.None));

        Assert.Equal(CreditPricing.SourceConfig, host.Provider.Source);
        Assert.Equal(1425, host.Provider.GetPack(300)!.UnitAmountMinor);
        Assert.Equal(0, host.RequestCount);
    }

    [Fact]
    public async Task Shadow_ServesTheConfigurationAndReportsDifferences()
    {
        using var host = new PricingTestHost("Shadow");
        host.EnqueuePriceList(PricingTestData.ImportedJson(1, new Dictionary<int, long> { [500] = 2300 }));

        await host.Refresher.RefreshAsync("test", conditional: true, CancellationToken.None);

        Assert.IsType<ConfigCreditPricingProvider>(host.Provider);
        Assert.Equal(2250, host.Provider.GetPack(500)!.UnitAmountMinor);
        var differences = host.Refresher.CompareWithConfig(host.Holder.Current!);
        var difference = Assert.Single(differences);
        Assert.Contains("500 credits", difference);
        Assert.Contains("2300", difference);
    }

    [Fact]
    public void Compare_OfTheImportedPriceList_FindsNoDifference()
    {
        var configured = new ConfigCreditPricingProvider(new BillingOptions()).GetPacks();
        var service = PriceListMapper.Map(PricingTestData.Snapshot(PricingTestData.ImportedJson()).Body, "EUR");

        Assert.Empty(PricingSnapshotRefresher.Compare(configured, service));
    }

    [Fact]
    public async Task NewVersion_DropsCachedStripePrices()
    {
        using var host = new PricingTestHost("On");
        host.EnqueuePriceList(PricingTestData.ImportedJson(1), "\"v1\"");
        await host.Refresher.RefreshAsync("test", conditional: true, CancellationToken.None);
        await host.Resolver.ResolveAsync(host.Provider.GetPack(300)!);
        await host.Resolver.ResolveAsync(host.Provider.GetPack(300)!);
        Assert.Equal(1, host.StripeLookups);

        host.EnqueuePriceList(PricingTestData.ImportedJson(2), "\"v2\"");
        await host.Refresher.RefreshAsync("test", conditional: true, CancellationToken.None);
        await host.Resolver.ResolveAsync(host.Provider.GetPack(300)!);

        Assert.Equal(2, host.StripeLookups);
    }

    [Fact]
    public async Task PriceEffective_RefreshesUnconditionallyAndDropsCachedPrices()
    {
        using var host = new PricingTestHost("On");
        host.EnqueuePriceList(PricingTestData.ImportedJson(1), "\"v1\"");
        await host.Refresher.RefreshAsync("test", conditional: true, CancellationToken.None);
        await host.Resolver.ResolveAsync(host.Provider.GetPack(300)!);
        host.EnqueuePriceList(PricingTestData.ImportedJson(1, new Dictionary<int, long> { [300] = 1450 }), "\"v1b\"");

        using (var scope = host.Services.CreateScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<IEnumerable<IBackofficeEventHandler>>();
            var handler = Assert.Single(registry.OfType<PriceEffectiveHandler>());
            using var data = JsonDocument.Parse("""{"version":1}""");
            await handler.HandleAsync(new BackofficeEvent("evt_1", BackofficeEventTypes.PriceEffective, data.RootElement.Clone(), "webhook", host.Time.GetUtcNow(), 1), CancellationToken.None);
        }
        await host.Resolver.ResolveAsync(host.Provider.GetPack(300)!);

        Assert.Empty(host.Request(1).Headers.IfNoneMatch);
        Assert.Equal(1450, host.Provider.GetPack(300)!.UnitAmountMinor);
        Assert.Equal(2, host.StripeLookups);
    }

    [Fact]
    public async Task Resync_FetchesUnconditionally()
    {
        using var host = new PricingTestHost("On");
        host.EnqueuePriceList(PricingTestData.ImportedJson(1), "\"v1\"");
        await host.Refresher.RefreshAsync("test", conditional: true, CancellationToken.None);
        host.EnqueuePriceList(PricingTestData.ImportedJson(1), "\"v1\"");

        using var scope = host.Services.CreateScope();
        var resync = Assert.Single(scope.ServiceProvider.GetServices<IBackofficeResync>());
        await resync.ResyncAsync("window_clamped", CancellationToken.None);

        Assert.Equal("pricing", resync.Name);
        Assert.Empty(host.Request(1).Headers.IfNoneMatch);
    }

    [Fact]
    public async Task Worker_LoadsTheSnapshotBeforeStartingAndRefreshesInTheBackground()
    {
        using var host = new PricingTestHost("On");
        await host.StoreAsync(PricingTestData.Snapshot(PricingTestData.ImportedJson(1, new Dictionary<int, long> { [100] = 700 })).Serialize());
        var gate = new TaskCompletionSource();
        host.Service.Responses.Enqueue(async _ =>
        {
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var worker = host.Services.GetServices<IHostedService>().OfType<PricingSnapshotWorker>().Single();

        await worker.StartAsync(CancellationToken.None);

        // Served from the stored snapshot while the service has not answered yet.
        Assert.Equal(700, host.Provider.GetPack(100)!.UnitAmountMinor);
        gate.SetResult();
        await worker.StopAsync(CancellationToken.None);
    }
}

public class PricingRegistrationTests
{
    [Fact]
    public void Off_UsesTheConfigurationAndStartsNothing()
    {
        using var host = new PricingTestHost("Off");

        Assert.IsType<ConfigCreditPricingProvider>(host.Provider);
        // Only the local price-change monitor (see PriceNoticeRegistrationTests).
        Assert.IsType<PriceChangeMonitorService>(Assert.Single(host.Services.GetServices<IHostedService>()));
        using var scope = host.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetServices<IBackofficeEventHandler>());
        Assert.Empty(scope.ServiceProvider.GetServices<IBackofficeResync>());
        Assert.Null(host.Services.GetService<PricingSnapshotRefresher>());
    }

    [Fact]
    public void Shadow_UsesTheConfigurationAndRefreshesInTheBackground()
    {
        using var host = new PricingTestHost("Shadow");

        Assert.IsType<ConfigCreditPricingProvider>(host.Provider);
        Assert.Single(host.Services.GetServices<IHostedService>().OfType<PricingSnapshotWorker>());
    }

    [Fact]
    public void On_UsesThePriceList()
    {
        using var host = new PricingTestHost("On");

        Assert.IsType<BackofficeCreditPricingProvider>(host.Provider);
        Assert.Single(host.Services.GetServices<IHostedService>().OfType<PricingSnapshotWorker>());
    }

    [Fact]
    public void Integration_WithoutSection_RegistersTheConfiguredPrices()
    {
        var services = DotNetSigningServer.Tests.Services.Backoffice.BackofficeRegistrationTests.Register(new());
        services.Configure<BillingOptions>(_ => { });
        using var provider = services.BuildServiceProvider();

        Assert.IsType<ConfigCreditPricingProvider>(provider.GetRequiredService<ICreditPricingProvider>());
        Assert.NotNull(provider.GetRequiredService<StripePriceResolver>());
    }
}
