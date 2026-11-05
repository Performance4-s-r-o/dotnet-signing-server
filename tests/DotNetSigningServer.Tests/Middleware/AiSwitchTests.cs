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

/// <summary>#83, #174: /api/ai/* is refused before anything else while the runtime setting ai_enabled is not on.</summary>
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

    /// <summary>Settings as they are without the service (closed) or with a fresh "on" from it.</summary>
    private static DotNetSigningServer.Services.Settings.IRuntimeSettings Options(string? raw) =>
        raw == "on" ? TestRuntimeSettings.AiOn() : raw == "env-off" ? EnvClosed() : TestRuntimeSettings.Defaults;

    private static DotNetSigningServer.Services.Settings.IRuntimeSettings EnvClosed()
    {
        var settings = TestRuntimeSettings.With(("AI_ENABLED", "false"));
        settings.Apply(DotNetSigningServer.Services.Settings.RuntimeSettingsSnapshot.Parse(
            """{"version":1,"items":[{"key":"ai_enabled","type":"bool","value":true,"fail_mode":"closed"}]}"""), DateTimeOffset.UtcNow);
        return settings;
    }

    private async Task<(DefaultHttpContext Context, int Calls, ExplodingBody Body)> RunAsync(DotNetSigningServer.Services.Settings.IRuntimeSettings ai, string path)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStringLocalizerFactory>(new KeyEchoLocalizerFactory());
        services.AddScoped(_ => _db);
        services.AddSingleton(ai);
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
        var (context, calls, _) = await RunAsync(Options("on"), "/api/ai/extract-data");

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
    [InlineData(null)]
    [InlineData("env-off")]
    public async Task Without_a_fresh_on_from_the_service_or_closed_by_env_it_is_403(string? state)
    {
        var (context, calls, _) = await RunAsync(Options(state), "/api/ai/extract-data");
        Assert.Equal(403, context.Response.StatusCode);
        Assert.Equal(0, calls);
    }
}
