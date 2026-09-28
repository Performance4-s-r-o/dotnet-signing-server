using System.Security.Claims;
using DotNetSigningServer.Filters;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Consents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Consents;

public class RequireCurrentConsentFilterTests
{
    private static readonly Guid UserId = Guid.Parse("3f0c2a1e-9b8d-4c7a-8f6e-1d2c3b4a5f60");

    private static readonly ConsentStatus Outstanding = new(
        [new OutstandingConsent("terms", "granted", 2, 1)], HasAnyRecord: true);

    private sealed class FakeStatusProvider : IConsentStatusProvider
    {
        public ConsentStatus Status { get; set; } = ConsentStatus.Current;
        public Exception? Error { get; set; }
        public int Calls { get; private set; }

        public Task<ConsentStatus> GetAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Error != null ? Task.FromException<ConsentStatus>(Error) : Task.FromResult(Status);
        }
    }

    private static async Task<(IActionResult? Result, bool NextCalled, FakeStatusProvider Provider)> RunAsync(
        string mode,
        ConsentStatus status,
        string path = "/Account",
        string method = "GET",
        string? authorization = null,
        string? authenticationType = "Cookies",
        string pathBase = "",
        string query = "",
        Exception? error = null)
    {
        var provider = new FakeStatusProvider { Status = status, Error = error };
        var services = new ServiceCollection().AddSingleton<IConsentStatusProvider>(provider).BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = method;
        http.Request.Path = path;
        http.Request.PathBase = pathBase;
        http.Request.QueryString = new QueryString(query);
        if (authorization != null) http.Request.Headers.Authorization = authorization;
        if (authenticationType != null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], authenticationType));
        }

        var options = Microsoft.Extensions.Options.Options.Create(new P4BackofficeProductOptions
        {
            Modules = { Consents = mode },
            DisabledReason = BackofficeDisabledReason.None,
        });
        var filter = new RequireCurrentConsentFilter(
            options, new ConsentStatusCache(new MemoryCache(new MemoryCacheOptions())), NullLogger<RequireCurrentConsentFilter>.Instance);

        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller: new object());
        var nextCalled = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, [], new object()));
        });
        return (context.Result, nextCalled, provider);
    }

    [Fact]
    public async Task On_OutstandingConsent_RedirectsToTheConsentPageWithTheReturnUrl()
    {
        var (result, next, _) = await RunAsync("On", Outstanding, path: "/Requests", query: "?page=2");

        Assert.False(next);
        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Account/Consent?returnUrl=%2FRequests%3Fpage%3D2", redirect.Url);
    }

    [Fact]
    public async Task On_KeepsTheLanguagePrefix()
    {
        var (result, _, _) = await RunAsync("On", Outstanding, path: "/Account", pathBase: "/cs");

        Assert.Equal("/cs/Account/Consent?returnUrl=%2Fcs%2FAccount", Assert.IsType<RedirectResult>(result).Url);
    }

    [Fact]
    public async Task On_FormPost_RedirectsWithoutReturnUrl()
    {
        var (result, _, _) = await RunAsync("On", Outstanding, path: "/Account/Settings", method: "POST");

        Assert.Equal("/Account/Consent", Assert.IsType<RedirectResult>(result).Url);
    }

    [Fact]
    public async Task On_CurrentConsent_LetsThrough()
    {
        var (result, next, _) = await RunAsync("On", ConsentStatus.Current);

        Assert.True(next);
        Assert.Null(result);
    }

    [Fact]
    public async Task Shadow_OnlyLogs()
    {
        var (result, next, provider) = await RunAsync("Shadow", Outstanding);

        Assert.True(next);
        Assert.Null(result);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Off_DoesNotEvenReadTheState()
    {
        var (result, next, provider) = await RunAsync("Off", Outstanding);

        Assert.True(next);
        Assert.Null(result);
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData("/api/sign")]
    [InlineData("/api/v1/pdf/fill")]
    [InlineData("/API/sign")]
    [InlineData("/api")]
    public async Task ApiPaths_AreNeverGated(string path)
    {
        var (result, next, provider) = await RunAsync("On", Outstanding, path: path);

        Assert.True(next);
        Assert.Null(result);
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData("Bearer p4pdf_abc")]
    [InlineData("bearer p4pdf_abc")]
    public async Task BearerTokens_AreNeverGated(string authorization)
    {
        var (result, next, provider) = await RunAsync("On", Outstanding, path: "/templates", authorization: authorization);

        Assert.True(next);
        Assert.Null(result);
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ApiKey")]
    public async Task NotSignedInWithTheCookie_IsNotGated(string? authenticationType)
    {
        var (result, next, provider) = await RunAsync("On", Outstanding, authenticationType: authenticationType);

        Assert.True(next);
        Assert.Null(result);
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData("/Account/Consent")]
    [InlineData("/Account/SignOut")]
    [InlineData("/Legal/TermsOfService")]
    [InlineData("/legal/privacypolicy")]
    [InlineData("/support")]
    [InlineData("/set-language")]
    public async Task ExemptPages_AreNotGated(string path)
    {
        var (result, next, _) = await RunAsync("On", Outstanding, path: path);

        Assert.True(next);
        Assert.Null(result);
    }

    [Fact]
    public async Task BrokenState_LetsTheRequestThrough()
    {
        var (result, next, _) = await RunAsync("On", Outstanding, error: new InvalidOperationException("db down"));

        Assert.True(next);
        Assert.Null(result);
    }
}
