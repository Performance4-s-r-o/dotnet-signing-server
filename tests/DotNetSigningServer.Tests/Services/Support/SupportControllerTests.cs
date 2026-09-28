using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Services.Support;

public class SupportControllerTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestHelpers.CreateInMemoryDbContext();
    private readonly User _user;
    private readonly OsTicketStub _osTicket = new();

    public SupportControllerTests()
    {
        _user = TestHelpers.CreateTestUser("jana@example.com");
        _db.Users.Add(_user);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    /// <summary>The cookie as <c>SignInUser</c> writes it: id and e-mail (as Name), no e-mail claim.</summary>
    private ClaimsPrincipal SignedIn() => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, _user.Id.ToString()),
        new Claim(ClaimTypes.Name, _user.Email),
    ], "test"));

    private SupportController Controller()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("osticket")).Returns(() => new HttpClient(_osTicket));
        var controller = new SupportController(
            _db,
            new StringLocalizer<SharedStrings>(new KeyEchoLocalizerFactory()),
            TestHelpers.WrapOptions(new OsTicketOptions { Url = "https://osticket.test", ApiKey = "k" }),
            factory.Object,
            NullLogger<SupportController>.Instance);
        var http = new DefaultHttpContext { User = SignedIn() };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        return controller;
    }

    [Fact]
    public async Task Off_TicketCarriesTheUsersEmailAndName()
    {
        _osTicket.Answer = "123456";

        await Controller().SubmitTicket("Subject", "Body", "billing", "normal");

        var body = Assert.Single(_osTicket.Bodies);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("jana@example.com", json.RootElement.GetProperty("email").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Off_IndexShowsTheReplyAddress()
    {
        var controller = Controller();

        Assert.IsType<ViewResult>(await controller.Index());

        Assert.Equal("jana@example.com", controller.ViewData["UserEmail"]);
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
