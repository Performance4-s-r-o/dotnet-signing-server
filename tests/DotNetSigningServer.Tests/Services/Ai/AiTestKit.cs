using System.Net;
using System.Text;
using System.Text.Json;
using DotNetSigningServer.Services.Ai;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Settings;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Ai;

/// <summary>Stands in for the AI providers: answers per host from queues and records every request.</summary>
internal sealed class ProviderStub : HttpMessageHandler
{
    public sealed record Sent(HttpRequestMessage Request, string Body);

    public List<Sent> Requests { get; } = new();
    public Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> Anthropic { get; } = new();
    public Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> Google { get; } = new();

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public void AnthropicAnswers(string outputJson, string stop = "end_turn") =>
        Anthropic.Enqueue((_, _) => Task.FromResult(Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            model = "claude-test",
            stop_reason = stop,
            content = new[] { new { type = "text", text = outputJson } },
            usage = new { input_tokens = 100, output_tokens = 20 },
        }))));

    public void AnthropicError(int status, string type, string message = "x") =>
        Anthropic.Enqueue((_, _) => Task.FromResult(Json((HttpStatusCode)status,
            JsonSerializer.Serialize(new { type = "error", error = new { type, message } }))));

    public void GoogleAnswers(string outputJson, string finish = "STOP") =>
        Google.Enqueue((_, _) => Task.FromResult(Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text = outputJson } } }, finishReason = finish } },
            usageMetadata = new { promptTokenCount = 50, candidatesTokenCount = 10 },
            modelVersion = "gemini-test",
        }))));

    public void GoogleError(int status, string statusName) =>
        Google.Enqueue((_, _) => Task.FromResult(Json((HttpStatusCode)status,
            JsonSerializer.Serialize(new { error = new { code = status, message = "x", status = statusName } }))));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests) Requests.Add(new Sent(request, body));
        var queue = request.RequestUri!.Host.Contains("anthropic") ? Anthropic : Google;
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? next;
        lock (Requests) next = queue.Count > 0 ? queue.Dequeue() : null;
        if (next == null) throw new InvalidOperationException("No answer queued for " + request.RequestUri);
        return await next(request, cancellationToken);
    }
}

/// <summary>Fixed credentials.</summary>
internal sealed class FixedCredentials(AiCredentialSet? set) : IAiCredentialsSource
{
    public HashSet<string> Cooling { get; } = new();

    public Task<AiCredentialSet?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(set);

    public void MarkCoolingDown(string providerId) => Cooling.Add(providerId);

    public bool IsCoolingDown(string providerId) => Cooling.Contains(providerId);
}

/// <summary>Gateway and service over the outbox test host, providers stubbed.</summary>
internal sealed class AiHost : IDisposable
{
    public const string AnthropicKey = "sk-test-anthropic-secret";
    public const string GoogleKey = "test-google-secret";

    public static AiCredentialSet Credentials(int maxProviders = 2, params AiProvider[] providers) =>
        new(true, DateTimeOffset.UtcNow.AddMinutes(10), ["eu"], maxProviders, providers.Length > 0 ? providers : [Anthropic(), Google()]);

    public static AiProvider Anthropic(string id = "11111111-1111-1111-1111-111111111111", string region = "eu", string kind = "api_key") =>
        new(id, "anthropic", "claude-test", region, new AiCredential(kind, AnthropicKey));

    public static AiProvider Google(string id = "22222222-2222-2222-2222-222222222222") =>
        new(id, "google", "gemini-test", "eu", new AiCredential("api_key", GoogleKey));

    public OutboxTestHost Outbox { get; }
    public ProviderStub Providers { get; } = new();
    public FixedCredentials Source { get; }

    public AiHost(AiCredentialSet? credentials = null, IRuntimeSettings? settings = null, TimeSpan? callTimeout = null, int aiMaxPages = 20)
    {
        Source = new FixedCredentials(credentials ?? Credentials());
        var s = settings ?? TestRuntimeSettings.AiOn();
        Outbox = new OutboxTestHost(configure: services =>
        {
            services.AddSingleton<IOutboxHandler, AiUsageOutboxHandler>();
            services.AddSingleton<IOutboxHandler, AiProviderEventOutboxHandler>();
            services.AddSingleton(s);
            services.AddSingleton<IAiCredentialsSource>(Source);
            services.AddHttpClient(AiProviderHttp.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Providers);
            services.AddSingleton<IAiProviderClient, AnthropicProviderClient>();
            services.AddSingleton<IAiProviderClient, GoogleProviderClient>();
            services.AddSingleton(sp => new AiGateway(
                sp.GetRequiredService<IRuntimeSettings>(), sp.GetRequiredService<IAiCredentialsSource>(),
                sp.GetServices<IAiProviderClient>(), sp.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System, NullLogger<AiGateway>.Instance)
            {
                CallTimeoutValue = callTimeout ?? AiGateway.CallTimeout,
            });
            services.AddSingleton(sp => new DotNetSigningServer.Services.TemplateAiService(
                sp.GetRequiredService<AiGateway>(),
                TestHelpers.WrapOptions(new DotNetSigningServer.Options.LimitsOptions { AiMaxPages = aiMaxPages }),
                NullLogger<DotNetSigningServer.Services.TemplateAiService>.Instance));
        });
    }

    public AiGateway Gateway => Outbox.Services.GetRequiredService<AiGateway>();
    public DotNetSigningServer.Services.TemplateAiService Service => Outbox.Services.GetRequiredService<DotNetSigningServer.Services.TemplateAiService>();

    /// <summary>Sends the queued outbox items to the stubbed service and returns (path, body) of each.</summary>
    public async Task<List<(string Path, string Body)>> DispatchedAsync()
    {
        while (await Outbox.Processor.DispatchDueAsync(CancellationToken.None) > 0) { }
        lock (Outbox.Service.Requests)
        {
            return Outbox.Service.Requests.Select(r => (r.Request.RequestUri!.AbsolutePath, r.Body)).ToList();
        }
    }

    public void Dispose() => Outbox.Dispose();
}

/// <summary>Test PDFs.</summary>
internal static class AiPdfs
{
    /// <summary>One page per entry; each draws its text with the given setup.</summary>
    public static byte[] Pages(params Action<PdfCanvas, PdfFont>[] pages)
    {
        using var ms = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfWriter(ms)))
        {
            var font = PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);
            foreach (var draw in pages)
            {
                var canvas = new PdfCanvas(pdf.AddNewPage());
                draw(canvas, font);
            }
        }
        return ms.ToArray();
    }

    public static Action<PdfCanvas, PdfFont> Text(string text, float x = 72, float y = 700, float size = 12, Color? color = null, int mode = 0) =>
        (canvas, font) =>
        {
            canvas.BeginText().SetFontAndSize(font, size).SetFillColor(color ?? ColorConstants.BLACK)
                .SetTextRenderingMode(mode).MoveText(x, y).ShowText(text).EndText();
        };

    public static Action<PdfCanvas, PdfFont> Both(params Action<PdfCanvas, PdfFont>[] parts) =>
        (canvas, font) => { foreach (var p in parts) p(canvas, font); };

    public static int PageCount(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return doc.GetNumberOfPages();
    }
}
