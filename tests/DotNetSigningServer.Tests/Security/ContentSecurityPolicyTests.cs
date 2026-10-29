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

    private static async Task<(string? Enforce, string? ReportOnly, string? Json)> Run(string? mode)
    {
        var mw = new ContentSecurityPolicyMiddleware(
            ctx => { ctx.Response.ContentType = ctx.Request.Path == "/json" ? "application/json" : "text/html"; return Task.CompletedTask; },
            Microsoft.Extensions.Options.Options.Create(new CspOptions { Mode = mode }),
            Microsoft.Extensions.Options.Options.Create(new P4BackofficeProductOptions()));
        var (html, f1) = NewContext();
        var (json, f2) = NewContext("/json");
        await mw.InvokeAsync(html); await f1.FireAsync();
        await mw.InvokeAsync(json); await f2.FireAsync();
        return (html.Response.Headers["Content-Security-Policy"].FirstOrDefault(),
            html.Response.Headers["Content-Security-Policy-Report-Only"].FirstOrDefault(),
            json.Response.Headers["Content-Security-Policy-Report-Only"].FirstOrDefault()
            ?? json.Response.Headers["Content-Security-Policy"].FirstOrDefault());
    }

    [Fact]
    public async Task Default_IsReportOnly_OnHtmlOnly()
    {
        var (enforce, report, json) = await Run(null);
        Assert.Null(enforce);
        Assert.NotNull(report);
        Assert.Null(json);
    }

    [Fact]
    public async Task Enforce_OnlyWhenSet()
    {
        var (enforce, report, _) = await Run("enforce");
        Assert.NotNull(enforce);
        Assert.Null(report);
    }

    [Fact]
    public async Task Off_SendsNoHeader()
    {
        var (enforce, report, _) = await Run("off");
        Assert.Null(enforce);
        Assert.Null(report);
    }

    [Fact]
    public async Task NoncesDifferPerRequest()
    {
        var a = (await Run("enforce")).Enforce;
        var b = (await Run("enforce")).Enforce;
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("", CspMode.ReportOnly)]
    [InlineData("report-only", CspMode.ReportOnly)]
    [InlineData("enforce", CspMode.Enforce)]
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
