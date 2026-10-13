using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Tests.Security;

/// <summary>
/// Issue #76: an API key (Authorization: Bearer) must never authenticate the account web UI.
/// Without Docker these checks are structural (scheme selection, attributes on every MVC action).
/// </summary>
public class WebUiAuthenticationSchemeTests
{
    private static HttpContext Request(string path, string? authorization)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        if (authorization != null) ctx.Request.Headers.Authorization = authorization;
        return ctx;
    }

    [Theory]
    [InlineData("/ApiTokens")]
    [InlineData("/Billing")]
    [InlineData("/Account/Settings/MaxConcurrent")]
    [InlineData("/templates")]
    [InlineData("/apitokens")]
    [InlineData("/api-docs")]
    public void Bearer_outside_api_is_routed_to_the_cookie(string path)
    {
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme,
            AuthenticationSchemeSelection.Select(Request(path, "Bearer some-token")));
    }

    [Theory]
    [InlineData("/api/presign")]
    [InlineData("/API/pdf-template")]
    [InlineData("/api")]
    public void Bearer_on_api_is_routed_to_the_api_key(string path)
    {
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName,
            AuthenticationSchemeSelection.Select(Request(path, "Bearer some-token")));
    }

    [Fact]
    public void Api_without_bearer_is_routed_to_the_cookie()
    {
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme,
            AuthenticationSchemeSelection.Select(Request("/api/pdf-template", null)));
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme,
            AuthenticationSchemeSelection.Select(Request("/api/pdf-template", "Basic abc")));
    }

    private static IEnumerable<Type> MvcControllers() =>
        typeof(ApiControllerBase).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .Where(t => !typeof(ApiControllerBase).IsAssignableFrom(t));

    /// <summary>
    /// Every [Authorize] on a web-UI controller or action must be cookie-only, so a new action
    /// with a bare [Authorize] fails here instead of quietly accepting API keys.
    /// </summary>
    [Fact]
    public void Every_authorize_on_mvc_controllers_is_cookie_only()
    {
        var offenders = new List<string>();
        foreach (var type in MvcControllers())
        {
            var members = new List<MemberInfo> { type };
            members.AddRange(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
            foreach (var member in members)
            {
                foreach (var data in member.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>())
                {
                    if (data.AuthenticationSchemes != CookieAuthenticationDefaults.AuthenticationScheme)
                    {
                        offenders.Add($"{type.Name}.{(member is Type ? "<class>" : member.Name)} ({data.AuthenticationSchemes ?? "default scheme"})");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Use [CookieAuthorize] instead of [Authorize] on web-UI controllers: " + string.Join(", ", offenders));
    }
}

/// <summary>The whole app on a throwaway PostgreSQL, with one user holding one server API token.</summary>
public sealed class WebUiAuthFixture : IAsyncLifetime
{
    public OutboxPostgresFixture Postgres { get; } = new();
    internal WebApplicationFactory<Program> App { get; private set; } = null!;
    public string ApiToken { get; private set; } = "";
    public Guid UserId { get; private set; }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;

        public Factory(string connectionString)
        {
            _connectionString = connectionString;
            ClientOptions.BaseAddress = new Uri("https://localhost");
            ClientOptions.AllowAutoRedirect = false;
        }

        private sealed class NoEmail : IEmailSender
        {
            public Task SendAsync(string toEmail, string subject, string htmlBody) => Task.CompletedTask;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("UseLocalDb", "false");
            builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString);
            builder.UseSetting("Token:Secret", "integration-test-secret-with-32-plus-characters");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IEmailSender>();
                services.AddScoped<IEmailSender, NoEmail>();
            });
        }
    }

    public async Task InitializeAsync()
    {
        var keys = Path.Combine(Path.GetTempPath(), "p4pdf-test-keys-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DATA_PROTECTION_KEYS_PATH", keys);
        await Postgres.InitializeAsync();
        App = new Factory(Postgres.ConnectionString);
        _ = App.Server; // start the host

        await using var db = Postgres.CreateContext();
        var user = new User { Email = $"u{Guid.NewGuid():N}@example.com", EmailVerified = true, IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        UserId = user.Id;

        using var scope = App.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
        var (plaintext, hash, _) = tokens.IssueToken(user, "integration", null);
        db.ApiTokens.Add(new ApiToken
        {
            UserId = user.Id,
            Label = "integration",
            TokenHash = hash,
            TokenPrefix = plaintext[..8],
            IsBrowserToken = false,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        ApiToken = plaintext;
    }

    /// <summary>A session cookie for the seeded user, minted with the app's own ticket format.</summary>
    public string SessionCookieHeader()
    {
        var options = App.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) },
            CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties(),
            CookieAuthenticationDefaults.AuthenticationScheme);
        var name = options.Cookie.Name ?? CookieAuthenticationDefaults.CookiePrefix + CookieAuthenticationDefaults.AuthenticationScheme;
        return $"{name}={options.TicketDataFormat.Protect(ticket)}";
    }

    public async Task DisposeAsync()
    {
        if (App != null) await App.DisposeAsync();
        await Postgres.DisposeAsync();
    }
}

[Trait("Category", "Db")]
public class WebUiBearerIsolationTests : IClassFixture<WebUiAuthFixture>
{
    private readonly WebUiAuthFixture _fx;

    public WebUiBearerIsolationTests(WebUiAuthFixture fx)
    {
        _fx = fx;
    }

    private HttpClient BearerClient()
    {
        var client = _fx.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _fx.ApiToken);
        return client;
    }

    private HttpClient CookieClient()
    {
        var client = _fx.App.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", _fx.SessionCookieHeader());
        return client;
    }

    private static bool IsRejected(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return true;
        if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther)
        {
            var location = response.Headers.Location?.ToString() ?? "";
            return location.Contains("/Account/SignIn", StringComparison.OrdinalIgnoreCase)
                   || location.Contains("/Account/Denied", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    [DockerFact]
    public async Task Api_token_works_on_the_api()
    {
        var response = await BearerClient().GetAsync("/api/pdf-template");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [DockerFact]
    public async Task Api_token_cannot_open_or_post_the_token_page()
    {
        var client = BearerClient();
        var get = await client.GetAsync("/ApiTokens");
        Assert.True(IsRejected(get), $"GET /ApiTokens with a bearer token answered {(int)get.StatusCode}");

        var post = await client.PostAsync("/ApiTokens", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["label"] = "stolen",
        }));
        Assert.True(IsRejected(post), $"POST /ApiTokens with a bearer token answered {(int)post.StatusCode}");
    }

    private static readonly Regex RouteParameter = new(@"\{(?<name>[^}:=?]+)(?<rest>[^}]*)\}");

    private static string Fill(string template) =>
        "/" + RouteParameter.Replace(template.TrimStart('/'), m =>
        {
            var rest = m.Groups["rest"].Value;
            if (rest.Contains(":guid")) return Guid.NewGuid().ToString();
            if (rest.Contains(":int") || rest.Contains(":long")) return "1";
            return "x";
        });

    /// <summary>
    /// Walks every routed MVC action (not the /api controllers) that requires authorization, so an
    /// action added later is covered without touching this test.
    /// </summary>
    [DockerFact]
    public async Task Api_token_is_rejected_by_every_authorized_mvc_action()
    {
        var endpoints = _fx.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (Endpoint: e, Action: e.Metadata.GetMetadata<ControllerActionDescriptor>()))
            .Where(x => x.Action != null
                        && !typeof(ApiControllerBase).IsAssignableFrom(x.Action.ControllerTypeInfo)
                        && x.Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any()
                        && x.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() == null)
            .ToList();

        var covered = endpoints.Select(x => x.Action!.ControllerName).Distinct().ToHashSet();
        foreach (var expected in new[] { "Account", "ApiTokens", "Billing", "Requests", "Support", "Home", "Admin" })
        {
            Assert.Contains(expected, covered);
        }

        var client = BearerClient();
        var failures = new List<string>();
        foreach (var (endpoint, action) in endpoints)
        {
            var path = Fill(endpoint.RoutePattern.RawText ?? "");
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (methods == null || methods.Count == 0) methods = new[] { "GET", "POST" };
            foreach (var method in methods)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (method != "GET" && method != "HEAD")
                {
                    request.Content = new FormUrlEncodedContent(new Dictionary<string, string>());
                }
                var response = await client.SendAsync(request);
                if (!IsRejected(response))
                {
                    failures.Add($"{method} {path} ({action!.ControllerName}.{action.ActionName}) -> {(int)response.StatusCode} {response.Headers.Location}");
                }
            }
        }

        Assert.True(failures.Count == 0, "Bearer API token reached the web UI:\n" + string.Join("\n", failures));
    }

    [DockerFact]
    public async Task Session_cookie_still_opens_the_web_ui()
    {
        var response = await CookieClient().GetAsync("/ApiTokens");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [DockerFact]
    public async Task Session_cookie_works_on_the_template_builder_endpoints()
    {
        var response = await CookieClient().GetAsync($"/api/pdf-template/{Guid.NewGuid()}");
        // An unknown id ends in the handler's own error (the service throws, the action
        // answers 5xx); what matters here is that the session got past authentication.
        Assert.DoesNotContain(response.StatusCode, new[]
        {
            HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.Redirect,
        });
    }

    [DockerFact]
    public async Task Session_cookie_is_refused_on_the_rest_of_the_api()
    {
        var client = CookieClient();
        var list = await client.GetAsync("/api/pdf-template");
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);

        var delete = await client.DeleteAsync($"/api/pdf-template/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
    }
}
