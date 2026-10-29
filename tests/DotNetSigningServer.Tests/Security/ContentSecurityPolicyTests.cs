using System.Text.RegularExpressions;
using DotNetSigningServer.Middleware;
using DotNetSigningServer.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace DotNetSigningServer.Tests.Security;

public class ContentSecurityPolicyTests
{
    /// <summary>DefaultHttpContext never fires OnStarting; this feature lets a test do it.</summary>
    private sealed class StartingFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _callbacks = [];
        public override void OnStarting(Func<object, Task> callback, object state) => _callbacks.Add((callback, state));
        public async Task FireAsync() { foreach (var (cb, st) in _callbacks) await cb(st); }
    }

    private static (DefaultHttpContext Context, StartingFeature Feature) NewContext(string path = "/")
    {
        var feature = new StartingFeature();
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IHttpResponseFeature>(feature);
        ctx.Request.Path = path;
        return (ctx, feature);
    }

    private static string RepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "dotnet-signing-server.csproj"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, relative);
    }

    [Fact]
    public void Policy_AllowsOnlyNoncedScripts_AndNoPluginsOrFraming()
    {
        var policy = ContentSecurityPolicyMiddleware.Build("abc", allowEval: false, [], null);

        Assert.Contains("script-src 'nonce-abc' 'strict-dynamic';", policy);
        Assert.DoesNotContain("unsafe-eval", policy);
        Assert.DoesNotContain("script-src 'self'", policy);
        Assert.Contains("object-src 'none'", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("base-uri 'self'", policy);
    }

    [Fact]
    public void Eval_IsOnlyAllowedWhenAPageAsks_AndWidgetOriginsAreListed()
    {
        var policy = ContentSecurityPolicyMiddleware.Build("n", true, ["https://legal.example.com"], "https://legal.example.com");

        Assert.Contains("'unsafe-eval'", policy);
        Assert.Contains("connect-src 'self' https://legal.example.com;", policy);
        Assert.Contains("frame-src 'self' https://legal.example.com;", policy);
    }

    [Fact]
    public async Task Middleware_SetsTheHeaderOnHtmlOnly_AndNoncesDiffer()
    {
        var mw = new ContentSecurityPolicyMiddleware(
            ctx => { ctx.Response.ContentType = ctx.Request.Path == "/json" ? "application/json" : "text/html"; return Task.CompletedTask; },
            Microsoft.Extensions.Options.Options.Create(new CspOptions()),
            Microsoft.Extensions.Options.Options.Create(new P4BackofficeProductOptions()));

        var (html1, f1) = NewContext();
        var (html2, f2) = NewContext();
        var (json, f3) = NewContext("/json");
        await mw.InvokeAsync(html1); await f1.FireAsync();
        await mw.InvokeAsync(html2); await f2.FireAsync();
        await mw.InvokeAsync(json); await f3.FireAsync();

        Assert.True(html1.Response.Headers.ContainsKey("Content-Security-Policy"));
        Assert.NotEqual(html1.Response.Headers["Content-Security-Policy"], html2.Response.Headers["Content-Security-Policy"]);
        Assert.False(json.Response.Headers.ContainsKey("Content-Security-Policy"));
    }

    [Theory]
    [InlineData("", CspMode.Enforce)]
    [InlineData("report-only", CspMode.ReportOnly)]
    [InlineData("OFF", CspMode.Off)]
    public void Mode_IsParsed(string value, CspMode expected) =>
        Assert.Equal(expected, new CspOptions { Mode = value }.EffectiveMode);

    [Fact]
    public void UnknownMode_RefusesStartup() =>
        Assert.Throws<InvalidOperationException>(() => new CspOptions { Mode = "enforced" }.EffectiveMode);

    [Fact]
    public void Views_HaveNoInlineEventHandlers()
    {
        var handler = new Regex(@"\son[a-z]+\s*=\s*""", RegexOptions.IgnoreCase);
        var offenders = Directory.EnumerateFiles(RepoFile("Views"), "*.cshtml", SearchOption.AllDirectories)
            .Where(f => handler.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Views_HaveNoJavascriptUrls()
    {
        var offenders = Directory.EnumerateFiles(RepoFile("Views"), "*.cshtml", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"(href|src|action)\s*=\s*""\s*javascript:", RegexOptions.IgnoreCase))
            .ToList();
        Assert.Empty(offenders);
    }
}
