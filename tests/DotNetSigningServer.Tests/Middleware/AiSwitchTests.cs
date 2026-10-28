using System.Security.Claims;
using DotNetSigningServer.Data;
using DotNetSigningServer.Middleware;
using DotNetSigningServer.Options;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace DotNetSigningServer.Tests.Middleware;

/// <summary>#83: /api/ai/* is refused before anything else while the AI switch is off.</summary>
[Collection("ApiGate")]
public class AiSwitchTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestHelpers.CreateInMemoryDbContext();

    public void Dispose() => _db.Dispose();

    private sealed class ExplodingBody : Stream
    {
        public bool Touched { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 1;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { Touched = true; throw new InvalidOperationException("body read"); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    private static AiOptions Options(string? raw)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(raw is null ? new Dictionary<string, string?>() : new() { ["AI:Enabled"] = raw })
            .Build();
        return new AiOptions { Enabled = AiSwitch.IsOn(config["AI:Enabled"]) };
    }

    private async Task<(DefaultHttpContext Context, int Calls, ExplodingBody Body)> RunAsync(AiOptions ai, string path)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStringLocalizerFactory>(new KeyEchoLocalizerFactory());
        services.AddScoped(_ => _db);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(ai));
        var provider = services.BuildServiceProvider();
        var calls = 0;
        var gate = new ApiGateMiddleware(_ => { calls++; return Task.CompletedTask; },
            Microsoft.Extensions.Options.Options.Create(new RateLimitOptions()));
        var body = new ExplodingBody();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = path;
        context.Request.Method = "POST";
        context.Request.Body = body;
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) }, "ApiKey"));
        await gate.InvokeAsync(context);
        return (context, calls, body);
    }

    [Theory]
    [InlineData("/api/ai/extract-data")]
    [InlineData("/api/ai/detect-fields")]
    [InlineData("/api/AI/anything-new")]
    public async Task Off_every_ai_route_is_403_AI_DISABLED_without_touching_the_body(string path)
    {
        var (context, calls, body) = await RunAsync(Options(null), path);

        Assert.Equal(403, context.Response.StatusCode);
        Assert.Equal(0, calls);
        Assert.False(body.Touched);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("AI_DISABLED", text);
    }

    [Fact]
    public async Task On_the_request_goes_through()
    {
        var (context, calls, _) = await RunAsync(Options("true"), "/api/ai/extract-data");

        Assert.Equal(1, calls);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task Other_routes_are_not_affected_by_the_switch()
    {
        var (_, calls, _) = await RunAsync(Options(null), "/api/presign");
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", false)]
    [InlineData("TRUE", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData("", false)]
    [InlineData(" true", false)]
    [InlineData("{{project.DOTNET_AI_ENABLED}}", false)]
    [InlineData(null, false)]
    public void Only_the_exact_text_true_turns_ai_on(string? raw, bool expected)
    {
        Assert.Equal(expected, AiSwitch.IsOn(raw));
        Assert.Equal(expected, Options(raw).Enabled);
    }
}
