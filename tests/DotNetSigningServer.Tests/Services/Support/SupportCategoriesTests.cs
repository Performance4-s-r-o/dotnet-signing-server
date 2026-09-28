using System.Net;
using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Inbox;
using DotNetSigningServer.Services.Support;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services.Backoffice.Email;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Support;

public class SupportCategoriesTests : IDisposable
{
    private const string Body = """
        {"locale":"cs","turnstile_sitekey":null,"data":[
          {"key":"signing","label":"Podepisování","requires_auth":false,"allow_attachments":true},
          {"key":"other","label":"Ostatní","requires_auth":false,"allow_attachments":true},
          {"key":"Bad Key","label":"x","requires_auth":false,"allow_attachments":false},
          {"key":"nolabel","label":"","requires_auth":false,"allow_attachments":false}
        ]}
        """;

    private readonly string _dbName = "support-" + Guid.NewGuid();
    private readonly StubServiceHandler _service = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _services;

    public SupportCategoriesTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(_dbName));
        services.AddHttpClient(SupportCategoriesClient.HttpClientName, c => c.BaseAddress = new Uri("https://backoffice.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => _service);
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private SupportCategoriesWorker Worker(SupportCategoriesHolder holder, BackofficeMode mode = BackofficeMode.On, ILogger<SupportCategoriesWorker>? logger = null) =>
        new(new SupportCategoriesClient(_services.GetRequiredService<IHttpClientFactory>()), holder,
            _services.GetRequiredService<IServiceScopeFactory>(), _time,
            logger ?? NullLogger<SupportCategoriesWorker>.Instance, mode);

    private void AnswerAll(string body)
    {
        foreach (var _ in CultureUrls.Supported) _service.Enqueue(HttpStatusCode.OK, body);
    }

    [Fact]
    public void FromResponse_KeepsValidCategoriesWithLabels()
    {
        var snapshot = SupportCategoriesSnapshot.FromResponse("cs", Body, _time.Now);

        Assert.Equal(["signing", "other"], snapshot.Categories.Select(c => c.Key));
        Assert.Equal("Podepisování", snapshot.Categories[0].Label);
    }

    [Fact]
    public void FromResponse_WithoutData_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => SupportCategoriesSnapshot.FromResponse("cs", """{"x":1}""", _time.Now));
    }

    [Fact]
    public async Task Refresh_StoresEveryLanguageAndPublishesIt()
    {
        AnswerAll(Body);
        var holder = new SupportCategoriesHolder();

        await Worker(holder).RefreshAsync(CancellationToken.None);

        Assert.Equal(CultureUrls.Supported.Length, _service.Requests.Count);
        Assert.Contains(_service.Requests, r => r.Request.RequestUri!.Query == "?locale=cs");
        Assert.Equal(["signing", "other"], holder.Get("cs")!.Categories.Select(c => c.Key));
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = SupportCategoriesSnapshot.TryDeserialize(
            await BackofficeStateStore.GetAsync(db, SupportCategoriesWorker.StateKey("de")));
        Assert.Equal(["signing", "other"], stored!.Categories.Select(c => c.Key));
    }

    [Fact]
    public async Task Stored_IsLoadedAtStartupWithoutTheService()
    {
        AnswerAll(Body);
        await Worker(new SupportCategoriesHolder()).RefreshAsync(CancellationToken.None);
        var requests = _service.Requests.Count;

        var fresh = new SupportCategoriesHolder();
        await Worker(fresh).LoadStoredAsync(CancellationToken.None);

        Assert.Equal("Podepisování", fresh.Get("en")!.Categories[0].Label);
        Assert.Equal(requests, _service.Requests.Count);
    }

    [Fact]
    public async Task ServiceDown_KeepsThePreviousList()
    {
        var holder = new SupportCategoriesHolder();
        var previous = new SupportCategoriesSnapshot("en", [new SupportCategoryOption("other", "Other")], _time.Now);
        holder.Set(previous);
        _service.Enqueue(HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<HttpRequestException>(() => Worker(holder).RefreshAsync(CancellationToken.None));

        Assert.Same(previous, holder.Get("en"));
    }

    [Fact]
    public async Task Shadow_LogsTheDifferenceToTheForm()
    {
        AnswerAll(Body);
        var logs = new ListLoggerProvider();

        await Worker(new SupportCategoriesHolder(), BackofficeMode.Shadow, logs.CreateLogger<SupportCategoriesWorker>())
            .RefreshAsync(CancellationToken.None);

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("billing"));
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("Shadow")]
    public void Provider_NotOn_OffersTheFormsList(string mode)
    {
        var holder = new SupportCategoriesHolder();
        holder.Set(new SupportCategoriesSnapshot("en", [new SupportCategoryOption("x", "X")], _time.Now));

        var provider = new SupportCategoriesProvider(holder, Options(mode));

        Assert.Same(SupportCategoriesProvider.Defaults, provider.For("en"));
    }

    [Fact]
    public void Provider_On_OffersTheServicesListForTheLanguage()
    {
        var holder = new SupportCategoriesHolder();
        holder.Set(new SupportCategoriesSnapshot("cs", [new SupportCategoryOption("x", "X")], _time.Now));
        var provider = new SupportCategoriesProvider(holder, Options("On"));

        Assert.Equal(["x"], provider.For("cs-CZ").Select(c => c.Key));
        Assert.Same(SupportCategoriesProvider.Defaults, provider.For("de"));
    }

    [Fact]
    public async Task TicketFailedEvent_IsLoggedAsAnError()
    {
        var logs = new ListLoggerProvider();
        var handler = new SupportEventsHandler(logs.CreateLogger<SupportEventsHandler>());
        using var data = JsonDocument.Parse(
            """{"id":"5b1c","category":"billing","subject_ref":"dotnet:user:1","status":"dead","reason":"retries_exhausted"}""");

        Assert.Contains(BackofficeEventTypes.SupportTicketFailed, handler.Types);
        await handler.HandleAsync(new BackofficeEvent("evt_1", BackofficeEventTypes.SupportTicketFailed, data.RootElement, "webhook", _time.Now, 1),
            CancellationToken.None);

        var error = Assert.Single(logs.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("5b1c", error.Message);
        Assert.Contains("retries_exhausted", error.Message);
    }

    private static Microsoft.Extensions.Options.IOptions<P4BackofficeProductOptions> Options(string mode) =>
        TestHelpers.WrapOptions(new P4BackofficeProductOptions
        {
            DisabledReason = BackofficeDisabledReason.None,
            Modules = { Support = mode },
        });
}
