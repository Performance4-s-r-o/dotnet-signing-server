using System.Net;
using System.Text.Json;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Tests.Services.Backoffice.Email;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetSigningServer.Tests.Services.Email;

public class TemplateShadowComparerTests
{
    private static readonly Dictionary<string, string> Vars = new()
    {
        ["otpCode"] = "123456",
        ["expiryMinutes"] = "10",
    };

    private static (TemplateShadowComparer Comparer, StubServiceHandler Service, ListLoggerProvider Logs, ServiceProvider Provider) Create()
    {
        var service = new StubServiceHandler();
        var logs = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddHttpClient(TemplateShadowComparer.HttpClientName, c => c.BaseAddress = new Uri(OutboxTestHost.BaseUrl))
            .ConfigurePrimaryHttpMessageHandler(() => service);
        var provider = services.BuildServiceProvider();
        var comparer = new TemplateShadowComparer(
            provider.GetRequiredService<IHttpClientFactory>(), logs.CreateLogger<TemplateShadowComparer>());
        return (comparer, service, logs, provider);
    }

    private static TemplateShadowRequest Request(string subject = "Code 123456", string html = "<html><head><title>x</title></head><body><p>Your code: <b>123456</b></p><p>Valid 10 min</p></body></html>") =>
        new(EmailTemplateId.TwoFactorCode, "en", Vars, subject, html);

    private static string Rendered(string subject, string text) =>
        JsonSerializer.Serialize(new { key = "two_factor_code", version = 3, locale = "en", subject, html = "<p/>", text });

    [Fact]
    public async Task SameSubjectAndText_LogsAgreement()
    {
        var (comparer, service, logs, provider) = Create();
        using var _ = provider;
        service.Enqueue(HttpStatusCode.OK, Rendered("Code 123456", "Your code:\n  123456\n\nValid 10 min"));

        await comparer.CompareAsync(Request(), CancellationToken.None);

        var (request, body) = Assert.Single(service.Requests);
        Assert.EndsWith("/v1/templates/two_factor_code/render", request.RequestUri!.AbsolutePath);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("en", json.RootElement.GetProperty("locale").GetString());
        Assert.Equal("123456", json.RootElement.GetProperty("variables").GetProperty("otpCode").GetString());
        Assert.Contains(logs.Entries, e => e.Message.Contains("agree"));
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Difference_IsLoggedWithVariablesMasked()
    {
        var (comparer, service, logs, provider) = Create();
        using var _ = provider;
        service.Enqueue(HttpStatusCode.OK, Rendered("Sign-in code 123456", "Your code: 123456. Valid 10 min"));

        await comparer.CompareAsync(Request(), CancellationToken.None);

        var warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("subject", warning.Message);
        Assert.Contains("text", warning.Message);
        Assert.Contains("{{otpCode}}", warning.Message);
        Assert.DoesNotContain("123456", warning.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task KeyWithoutTemplatesRead_TurnsTheComparisonOff(HttpStatusCode status)
    {
        var (comparer, service, _, provider) = Create();
        using var __ = provider;
        service.EnqueueProblem(status, "insufficient_scope");

        await comparer.CompareAsync(Request(), CancellationToken.None);
        await comparer.CompareAsync(Request(), CancellationToken.None);

        Assert.True(comparer.Disabled);
        Assert.Single(service.Requests);
    }

    [Fact]
    public async Task ServiceFailure_IsSwallowed()
    {
        var (comparer, service, logs, provider) = Create();
        using var _ = provider;
        service.Responses.Enqueue(_ => throw new HttpRequestException("down"));

        await comparer.CompareAsync(Request(), CancellationToken.None);

        Assert.False(comparer.Disabled);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("HttpRequestException"));
    }

    [Fact]
    public async Task Schedule_IsProcessedInTheBackground()
    {
        var (comparer, service, _, provider) = Create();
        using var _ = provider;
        service.Enqueue(HttpStatusCode.OK, Rendered("Code 123456", "Your code: 123456 Valid 10 min"));

        await comparer.StartAsync(CancellationToken.None);
        comparer.Schedule(EmailTemplateId.TwoFactorCode, "en", Vars.ToDictionary(v => v.Key, v => (string?)v.Value),
            new EmailTemplateResult("Code 123456", "<p>Your code: 123456</p><p>Valid 10 min</p>"));
        for (var i = 0; i < 100 && service.Requests.Count == 0; i++) await Task.Delay(20);
        await comparer.StopAsync(CancellationToken.None);

        Assert.Single(service.Requests);
    }

    [Fact]
    public void Diff_NormalisesWhitespaceAndStripsMarkup()
    {
        var differences = TemplateShadowDiff.Describe(
            "Hello", "<!-- c --><style>p{}</style><p>A&amp;B</p>\n<br/>  <span>C</span>", "Hello", "A&B C",
            new Dictionary<string, string>());

        Assert.Empty(differences);
    }

    [Fact]
    public void Diff_MasksLongerValuesFirst()
    {
        var masked = TemplateShadowDiff.Normalize(
            "https://x/Billing and https://x/Billing/Cancel",
            new Dictionary<string, string> { ["billingUrl"] = "https://x/Billing", ["cancelUrl"] = "https://x/Billing/Cancel" });

        Assert.Equal("{{billingUrl}} and {{cancelUrl}}", masked);
    }
}
