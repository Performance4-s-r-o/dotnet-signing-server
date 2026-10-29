using System.Net;
using System.Net.Sockets;
using System.Text;
using DotNetSigningServer.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// #72: the real Kestrel, configured by <see cref="RequestLimits"/>, against a client that sends
/// slowly or goes away. Raw sockets on loopback; the limits are shortened by configuration so the
/// tests run in seconds, the code path is the production one.
/// </summary>
public class SlowRequestLimitsTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private int _port;
    private readonly TaskCompletionSource<Exception?> _bodyRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Limits:RequestHeadersTimeoutSeconds"] = "1",
            ["Limits:MinRequestBodyBytesPerSecond"] = (20 * 1024).ToString(),
            ["Limits:MinRequestBodyGraceSeconds"] = "2",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(k => RequestLimits.Apply(k.Limits, builder.Configuration));
        _app = builder.Build();
        _app.MapPost("/upload", async (HttpContext ctx) =>
        {
            ctx.RequestAborted.Register(() => _aborted.TrySetResult(true));
            try
            {
                await ctx.Request.Body.CopyToAsync(Stream.Null, ctx.RequestAborted);
                _bodyRead.TrySetResult(null);
            }
            catch (Exception ex)
            {
                _bodyRead.TrySetResult(ex);
            }
        });
        await _app.StartAsync();
        _port = new Uri(_app.Urls.First()).Port;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<TcpClient> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        return client;
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private const string BodyHeaders = "POST /upload HTTP/1.1\r\nHost: a\r\nContent-Type: application/octet-stream\r\nContent-Length: 1000000\r\n\r\n";

    [Fact]
    public async Task Headers_that_never_finish_are_cut()
    {
        using var client = await ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(Ascii("POST /upload HTTP/1.1\r\nHost: a\r\n")); // no blank line, ever

        var buffer = new byte[512];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        // Kestrel answers 408 or just closes; either way the connection does not stay open.
        if (read > 0) Assert.Contains("408", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task A_body_dripping_below_the_minimum_rate_times_out()
    {
        using var client = await ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(Ascii(BodyHeaders));

        var drip = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 100; i++)
                {
                    await stream.WriteAsync(new byte[] { 1 });
                    await Task.Delay(200);
                }
            }
            catch (IOException) { /* the server closed it: the point */ }
        });

        var error = await _bodyRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(error);
        Assert.IsType<Microsoft.AspNetCore.Server.Kestrel.Core.BadHttpRequestException>(error);
        await drip.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_client_that_disconnects_mid_body_aborts_the_request()
    {
        var client = await ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(Ascii(BodyHeaders));
        await stream.WriteAsync(new byte[1000]);
        await Task.Delay(200);

        client.Client.LingerState = new LingerOption(true, 0); // RST, as a crashed client
        client.Dispose();

        // The read of the body fails or the token fires, whichever the server notices first; a handler
        // that was reading stops either way, within seconds.
        Assert.NotNull(await _bodyRead.Task.WaitAsync(TimeSpan.FromSeconds(12)));
    }
}
