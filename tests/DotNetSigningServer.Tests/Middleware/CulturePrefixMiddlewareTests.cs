using DotNetSigningServer.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;

namespace DotNetSigningServer.Tests.Middleware;

public class CulturePrefixMiddlewareTests
{
    private sealed record Result(HttpContext Context, bool NextCalled)
    {
        public int Status => Context.Response.StatusCode;
        public string? Location => Context.Response.Headers.Location.ToString() is { Length: > 0 } l ? l : null;
        public string? Culture => Context.Items[CulturePrefixMiddleware.CultureItemKey] as string;

        /// <summary>The Set-Cookie this response wrote, in the form a browser sends back.</summary>
        public string? CookieHeader
        {
            get
            {
                var setCookie = Context.Response.Headers.SetCookie.ToString();
                return string.IsNullOrEmpty(setCookie) ? null : setCookie.Split(';')[0];
            }
        }
    }

    private static async Task<Result> InvokeAsync(
        string path,
        string method = "GET",
        string? query = null,
        string? cookieHeader = null,
        string accept = "text/html",
        string? acceptLanguage = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (!string.IsNullOrEmpty(query))
        {
            context.Request.QueryString = new QueryString(query);
        }

        context.Request.Headers.Accept = accept;
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            context.Request.Headers.Cookie = cookieHeader;
        }

        if (!string.IsNullOrEmpty(acceptLanguage))
        {
            context.Request.Headers.AcceptLanguage = acceptLanguage;
        }

        var nextCalled = false;
        var middleware = new CulturePrefixMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.Invoke(context);
        return new Result(context, nextCalled);
    }

    /// <summary>The Cookie header a visitor carries after being served a Spanish page.</summary>
    private static async Task<string> RememberedSpanishCookieAsync()
    {
        var served = await InvokeAsync("/es/pricing");
        return Assert.IsType<string>(served.CookieHeader);
    }

    [Fact]
    public async Task PrefixedPath_MovesTheLocaleIntoPathBase_AndRemembersIt()
    {
        var result = await InvokeAsync("/es/pricing");

        Assert.True(result.NextCalled);
        Assert.Equal("/es", result.Context.Request.PathBase.Value);
        Assert.Equal("/pricing", result.Context.Request.Path.Value);
        Assert.Equal("es", result.Culture);
        Assert.Contains(CookieRequestCultureProvider.DefaultCookieName, result.CookieHeader ?? string.Empty);
    }

    [Fact]
    public async Task RememberedLocale_SendsACleanUrlToItsPrefixedTwin()
    {
        var result = await InvokeAsync("/pricing", cookieHeader: await RememberedSpanishCookieAsync());

        Assert.False(result.NextCalled);
        Assert.Equal(302, result.Status);
        Assert.Equal("/es/pricing", result.Location);
    }

    [Fact]
    public async Task EnglishPrefix_SwitchesAVisitorWhoIsRememberedAsSpanish()
    {
        // The bug this covers: the switcher used to link English to the clean URL,
        // which the remembered Spanish read as "no preference" and bounced straight
        // back to /es — the click looked like a plain page refresh.
        var switched = await InvokeAsync("/en/pricing", cookieHeader: await RememberedSpanishCookieAsync());

        Assert.False(switched.NextCalled);
        Assert.Equal(302, switched.Status);
        Assert.Equal("/pricing", switched.Location);
        Assert.Equal("no-store", switched.Context.Response.Headers.CacheControl.ToString());

        // …and the choice sticks: the clean URL now renders English instead of redirecting.
        var landed = await InvokeAsync("/pricing", cookieHeader: switched.CookieHeader);

        Assert.True(landed.NextCalled);
        Assert.Equal(200, landed.Status);
        Assert.Equal("en", landed.Culture);
    }

    [Fact]
    public async Task EnglishPrefix_OnTheHomePage_RedirectsToTheRoot()
    {
        var result = await InvokeAsync("/en", cookieHeader: await RememberedSpanishCookieAsync());

        Assert.Equal(302, result.Status);
        Assert.Equal("/", result.Location);
    }

    [Fact]
    public async Task EnglishPrefix_KeepsTheQueryString()
    {
        var result = await InvokeAsync("/en/Requests", query: "?page=2");

        Assert.Equal(302, result.Status);
        Assert.Equal("/Requests?page=2", result.Location);
    }

    [Fact]
    public async Task EnglishPrefix_OnAPost_IsServedInPlaceInsteadOfLosingTheBody()
    {
        var result = await InvokeAsync("/en/Account/SignIn", method: "POST");

        Assert.True(result.NextCalled);
        Assert.Equal(200, result.Status);
        Assert.Equal("/Account/SignIn", result.Context.Request.Path.Value);
        Assert.Equal("en", result.Culture);
    }

    [Theory]
    [InlineData("en", "/en/pricing")]
    [InlineData("cs", "/cs/pricing")]
    public async Task LegacyCultureQuery_RedirectsToThePrefixThatRecordsTheChoice(string culture, string expected)
    {
        var result = await InvokeAsync("/pricing", query: $"?culture={culture}");

        Assert.Equal(301, result.Status);
        Assert.Equal(expected, result.Location);
    }

    [Fact]
    public async Task BrowserPreference_DecidesOnlyWhenNothingIsRemembered()
    {
        var guided = await InvokeAsync("/pricing", acceptLanguage: "es-ES,es;q=0.9");
        Assert.Equal(302, guided.Status);
        Assert.Equal("/es/pricing", guided.Location);

        // No cookie, no Accept-Language — crawlers and curl stay on the English URL.
        var bare = await InvokeAsync("/pricing");
        Assert.True(bare.NextCalled);
        Assert.Equal(200, bare.Status);
    }

    [Fact]
    public async Task NonPageRequests_AreNeverRedirected()
    {
        var cookie = await RememberedSpanishCookieAsync();

        var api = await InvokeAsync("/api/v1/sign", method: "POST", cookieHeader: cookie, accept: "application/json");
        Assert.True(api.NextCalled);

        var asset = await InvokeAsync("/css/custom.css", cookieHeader: cookie, accept: "text/css");
        Assert.True(asset.NextCalled);

        var health = await InvokeAsync("/health", cookieHeader: cookie);
        Assert.True(health.NextCalled);
    }
}
