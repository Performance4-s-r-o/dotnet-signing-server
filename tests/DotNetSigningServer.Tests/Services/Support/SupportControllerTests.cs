using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Support;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Services.Support;

/// <summary>
/// The support form: e-mail from the database (Off and On), osTicket directly while Off,
/// outbox + immediate attempt while On. The service is a stub; nothing leaves the process.
/// </summary>
public class SupportControllerTests : IDisposable
{
    private readonly OutboxTestHost _host = new(configure: s =>
        s.AddSingleton<IOutboxHandler>(new SupportTicketOutboxHandler(NullLogger<SupportTicketOutboxHandler>.Instance)));
    private readonly IServiceScope _scope;
    private readonly ApplicationDbContext _db;
    private readonly User _user;
    private readonly OsTicketStub _osTicket = new();
    private readonly SupportCategoriesHolder _categories = new();

    public SupportControllerTests()
    {
        _scope = _host.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        _user = TestHelpers.CreateTestUser("jana@example.com");
        _db.Users.Add(_user);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _scope.Dispose();
        _host.Dispose();
    }

    private SupportController Controller(string? supportMode = null, bool osTicketConfigured = true) =>
        Create(_host, _scope, _user, _osTicket, _categories, supportMode, osTicketConfigured);

    /// <summary>The controller as MVC would build it, over <paramref name="host"/>'s database and outbox.</summary>
    internal static SupportController Create(
        OutboxTestHost host, IServiceScope scope, User user, OsTicketStub osTicket, SupportCategoriesHolder categories,
        string? supportMode = null, bool osTicketConfigured = true)
    {
        // The request's language, as request localization sets it (flows into the test's awaits).
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("osticket")).Returns(() => new HttpClient(osTicket));
        var backoffice = TestHelpers.WrapOptions(new P4BackofficeProductOptions
        {
            DisabledReason = BackofficeDisabledReason.None,
            Modules = { Support = supportMode },
        });
        var controller = new SupportController(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            new StringLocalizer<SharedStrings>(new KeyEchoLocalizerFactory()),
            TestHelpers.WrapOptions(osTicketConfigured ? new OsTicketOptions { Url = "https://osticket.test", ApiKey = "k" } : new OsTicketOptions()),
            factory.Object,
            NullLogger<SupportController>.Instance,
            backoffice,
            new SupportCategoriesProvider(categories, backoffice),
            scope.ServiceProvider.GetRequiredService<IBackofficeOutbox>(),
            host.Processor,
            host.Time);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                // The cookie as SignInUser writes it: id and e-mail (as Name), no e-mail claim.
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Email),
            ], "test")),
        };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("app.example.com");
        http.Request.Headers.UserAgent = "Mozilla/5.0 (test)";
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        return controller;
    }

    private Task<List<BackofficeOutboxItem>> ItemsAsync() =>
        _host.WithDbAsync(db => db.BackofficeOutboxItems.AsNoTracking().ToListAsync());

    // ── Off ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Off_TicketCarriesTheUsersEmailAndName()
    {
        _osTicket.Answer = "123456";
        var controller = Controller();

        await controller.SubmitTicket("Subject", "Body", "billing", "normal");

        var body = Assert.Single(_osTicket.Bodies);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("jana@example.com", json.RootElement.GetProperty("email").GetString());
        Assert.Equal("jana@example.com", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("SupportTicketCreatedWithId".Replace("{0}", "123456"), controller.TempData["Info"]);
        Assert.Empty(await ItemsAsync());
        Assert.Empty(_host.Service.Requests);
    }

    [Fact]
    public async Task Off_IndexShowsTheReplyAddressAndTheFormsCategories()
    {
        var controller = Controller();

        Assert.IsType<ViewResult>(await controller.Index());

        Assert.Equal("jana@example.com", controller.ViewData["UserEmail"]);
        Assert.Same(SupportCategoriesProvider.Defaults, controller.ViewData["SupportCategories"]);
    }

    [Fact]
    public async Task Shadow_StillSendsToOsTicket()
    {
        await Controller("Shadow").SubmitTicket("Subject", "Body", "billing", "normal");

        Assert.Single(_osTicket.Bodies);
        Assert.Empty(await ItemsAsync());
    }

    [Fact]
    public async Task NeitherOnNorOsTicket_Is404()
    {
        var controller = Controller("Off", osTicketConfigured: false);

        Assert.IsType<NotFoundResult>(await controller.Index());
        Assert.IsType<NotFoundResult>(await controller.SubmitTicket("s", "m", "other", "normal"));
    }

    // ── On ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task On_WithoutOsTicketConfiguration_ShowsTheForm()
    {
        Assert.IsType<ViewResult>(await Controller("On", osTicketConfigured: false).Index());
    }

    [Fact]
    public async Task On_ServiceAnswers_ShowsTheTicketNumber()
    {
        _host.Service.Enqueue(HttpStatusCode.Created,
            """{"id":"5b1c","status":"submitted","ticket_number":"482193","portal_url":null}""");
        var controller = Controller("On", osTicketConfigured: false);

        await controller.SubmitTicket("Invoice", "<script>alert(1)</script>", "billing", "high", Guid.NewGuid().ToString("N"));

        Assert.Equal("SupportTicketCreatedWithId".Replace("{0}", "482193"), controller.TempData["Info"]);
        Assert.Null(controller.TempData["Error"]);
        var item = Assert.Single(await ItemsAsync());
        Assert.Equal(BackofficeOutboxStatus.Sent, item.Status);
        Assert.StartsWith($"dotnet:user:{_user.Id:D}/support/", item.SubjectRef);

        var (request, body) = Assert.Single(_host.Service.Requests);
        Assert.Equal(item.Id.ToString(), request.Headers.GetValues(OutboxRequest.IdempotencyKeyHeader).Single());
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("billing", root.GetProperty("category").GetString());
        Assert.Equal("<script>alert(1)</script>", root.GetProperty("message").GetString());
        Assert.Equal("jana@example.com", root.GetProperty("reporter").GetProperty("email").GetString());
        Assert.Equal("jana@example.com", root.GetProperty("reporter").GetProperty("name").GetString());
        Assert.Equal($"dotnet:user:{_user.Id:D}", root.GetProperty("reporter").GetProperty("subject_ref").GetString());
        Assert.Equal("https://app.example.com/support", root.GetProperty("context").GetProperty("url").GetString());
        Assert.Equal("Mozilla/5.0 (test)", root.GetProperty("context").GetProperty("user_agent").GetString());
        Assert.Empty(_osTicket.Bodies);
    }

    [Fact]
    public async Task On_ServiceQueues_ShowsReceived()
    {
        _host.Service.Enqueue(HttpStatusCode.Accepted, """{"id":"5b1c","status":"queued","ticket_number":null,"portal_url":null}""");
        var controller = Controller("On");

        await controller.SubmitTicket("s", "m", "other", "normal");

        Assert.Equal("SupportTicketQueued", controller.TempData["Info"]);
        Assert.Equal(BackofficeOutboxStatus.Sent, Assert.Single(await ItemsAsync()).Status);
    }

    [Fact]
    public async Task On_ServiceDown_ShowsReceivedAndKeepsTheTicketQueued()
    {
        _host.Service.Responses.Enqueue(_ => throw new HttpRequestException("connection refused"));
        var controller = Controller("On");

        var result = await controller.SubmitTicket("s", "m", "other", "normal");

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("SupportTicketQueued", controller.TempData["Info"]);
        Assert.Null(controller.TempData["Error"]);
        var item = Assert.Single(await ItemsAsync());
        Assert.Equal(BackofficeOutboxStatus.Pending, item.Status);
        Assert.NotNull(item.PayloadProtected);
        Assert.Empty(_osTicket.Bodies);
    }

    [Fact]
    public async Task On_SameFormSubmittedTwice_IsQueuedOnce()
    {
        _host.Service.Enqueue(HttpStatusCode.Created, """{"id":"5b1c","status":"submitted","ticket_number":"9","portal_url":null}""");
        var formKey = Guid.NewGuid().ToString("N");

        await Controller("On").SubmitTicket("s", "m", "other", "normal", formKey);
        var again = Controller("On");
        await again.SubmitTicket("s", "m", "other", "normal", formKey);

        Assert.Single(await ItemsAsync());
        Assert.Single(_host.Service.Requests);
        Assert.Equal("SupportTicketCreatedWithId".Replace("{0}", "9"), again.TempData["Info"]);
    }

    [Fact]
    public async Task On_OffersAndValidatesTheServicesCategories()
    {
        _categories.Set(new SupportCategoriesSnapshot("en",
            [new SupportCategoryOption("signing", "Signing"), new SupportCategoryOption("other", "Other")], _host.Time.Now));
        var index = Controller("On");
        await index.Index();
        var offered = Assert.IsAssignableFrom<IReadOnlyList<SupportCategoryOption>>(index.ViewData["SupportCategories"]);
        Assert.Equal(["signing", "other"], offered.Select(c => c.Key));

        await Controller("On").SubmitTicket("s", "m", "billing", "normal");

        using var json = JsonDocument.Parse(Assert.Single(_host.Service.Requests).Body);
        Assert.Equal("other", json.RootElement.GetProperty("category").GetString());
    }

    [Fact]
    public async Task On_EmptyFields_AreRefusedWithoutQueueing()
    {
        var controller = Controller("On");

        await controller.SubmitTicket(" ", "m", "other", "normal");

        Assert.Equal("FieldsRequired", controller.TempData["Error"]);
        Assert.Empty(await ItemsAsync());
    }

    internal sealed class OsTicketStub : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        public string Answer { get; set; } = "1";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(Answer, Encoding.UTF8, "text/plain") };
        }
    }
}
