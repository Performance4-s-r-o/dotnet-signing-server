using System.Net;
using System.Text.Json;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Email;
using Microsoft.Extensions.Logging;

namespace DotNetSigningServer.Tests.Services.Backoffice.Email;

public class EmailTemplateOutboxHandlerTests
{
    private static readonly IReadOnlyDictionary<string, string?> Reset =
        EmailTemplateVariables.PasswordReset("https://app.example.com/Account/ResetPassword?token=abc", 60);

    [Fact]
    public async Task Queued_IsPostedInTemplateModeWithoutTheLocalCopy()
    {
        using var host = new TemplatedEmailTestHost("On", EmailTemplateId.PasswordReset);
        await host.SendAsync(EmailTemplateId.PasswordReset, Reset, locale: "cs");
        var id = Assert.Single(await host.ItemsAsync()).Id;
        host.Host.Service.Enqueue(HttpStatusCode.Accepted, """{"id":"eml_1","status":"queued"}""");

        await host.Host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await host.Host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Sent, item.Status);
        Assert.Equal("eml_1", item.RemoteId);
        Assert.Null(item.PayloadProtected);

        var (request, body) = Assert.Single(host.Host.Service.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/v1/emails", request.RequestUri!.AbsolutePath);
        Assert.Equal(id.ToString(), request.Headers.GetValues(OutboxRequest.IdempotencyKeyHeader).Single());
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("password_reset", root.GetProperty("template").GetString());
        Assert.Equal("cs", root.GetProperty("locale").GetString());
        Assert.Equal(
            ["expiryMinutes", "resetUrl"],
            root.GetProperty("variables").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("60", root.GetProperty("variables").GetProperty("expiryMinutes").GetString());
        Assert.False(root.TryGetProperty("fallback", out _));
        Assert.False(root.TryGetProperty("subject", out _));
        Assert.False(root.TryGetProperty("html", out _));
        Assert.Empty(host.Email.DirectSends);
    }

    [Fact]
    public async Task TemplateVariablesInvalid_IsDeadWithAnErrorAndNoRetry()
    {
        var logs = new ListLoggerProvider();
        using var host = new TemplatedEmailTestHost("On", EmailTemplateId.PaymentFailed);
        var handler = new EmailTemplateOutboxHandler(logs.CreateLogger<EmailTemplateOutboxHandler>());
        await host.SendAsync(EmailTemplateId.PaymentFailed,
            EmailTemplateVariables.PaymentFailed("purchase", "10.00", "EUR", "card_declined", "https://app.example.com/Billing"));
        var item = Assert.Single(await host.ItemsAsync());

        host.Host.Service.EnqueueProblem(HttpStatusCode.UnprocessableEntity, EmailTemplateOutboxHandler.TemplateVariablesInvalid);
        var http = host.Host.Services.GetHttpClient();
        var result = await handler.SendAsync(
            new OutboxRequest(http, item.Id, item.Kind, host.Payload(item).RootElement.GetRawText(), 1, false, null),
            CancellationToken.None);

        Assert.Equal(OutboxOutcome.Dead, RetryPolicy.Classify(result));
        var error = Assert.Single(logs.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("payment_failed", error.Message);
        Assert.Contains(item.Id.ToString(), error.Message);
    }

    [Fact]
    public async Task TemplateVariablesInvalid_ThroughTheDispatcher_EndsDead()
    {
        using var host = new TemplatedEmailTestHost("On", EmailTemplateId.PaymentFailed);
        await host.SendAsync(EmailTemplateId.PaymentFailed,
            EmailTemplateVariables.PaymentFailed("purchase", "10.00", "EUR", "card_declined", "https://app.example.com/Billing"));
        var id = Assert.Single(await host.ItemsAsync()).Id;
        host.Host.Service.EnqueueProblem(HttpStatusCode.UnprocessableEntity, EmailTemplateOutboxHandler.TemplateVariablesInvalid);

        await host.Host.Processor.DispatchDueAsync(CancellationToken.None);
        host.Host.Time.Advance(TimeSpan.FromDays(1));
        await host.Host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await host.Host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Dead, item.Status);
        Assert.Equal(1, item.Attempts);
        Assert.Contains(EmailTemplateOutboxHandler.TemplateVariablesInvalid, item.LastError);
        Assert.Single(host.Host.Service.Requests);
    }

    [Fact]
    public async Task CriticalTemplate_BreakGlassSendsTheLocalRendering()
    {
        using var host = new TemplatedEmailTestHost("On", EmailTemplateId.PasswordReset);
        await host.SendAsync(EmailTemplateId.PasswordReset, Reset, locale: "en");
        var id = Assert.Single(await host.ItemsAsync()).Id;

        host.Host.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await host.Email.Fallback.RunAsync(CancellationToken.None));

        var (to, subject, html) = Assert.Single(host.Email.DirectSends);
        Assert.Equal("jan@example.com", to);
        Assert.False(string.IsNullOrWhiteSpace(subject));
        Assert.Contains("https://app.example.com/Account/ResetPassword?token=abc", html);
        var item = await host.Host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.FallbackSent, item.Status);
        Assert.Null(item.PayloadProtected);
        Assert.Empty(host.Host.Service.Requests);
    }

    [Fact]
    public async Task CriticalTemplateWithoutLocalCopy_IsLeftToTheService()
    {
        using var host = new TemplatedEmailTestHost("On");
        var id = await host.Host.EnqueueAsync(
            TemplatedEmailSender.BuildPayload(EmailTemplateId.TwoFactorCode, "jan@example.com", "cs",
                EmailTemplateVariables.TwoFactorCode("123456", 10), new EmailSendOptions(EmailTemplateId.TwoFactorCode, "cs")),
            critical: true,
            kind: TemplatedEmailSender.OutboxKind);

        host.Host.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, await host.Email.Fallback.RunAsync(CancellationToken.None));

        Assert.Empty(host.Email.DirectSends);
        var item = await host.Host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Pending, item.Status);
        Assert.NotNull(item.PayloadProtected);
    }

    [Fact]
    public void WithoutFallback_RemovesOnlyTheLocalCopy()
    {
        var body = EmailTemplateOutboxHandler.WithoutFallback(
            """{"to":"a@example.com","template":"two_factor_code","variables":{"otpCode":"1"},"fallback":{"subject":"s","html":"h"}}""");

        Assert.Equal("""{"to":"a@example.com","template":"two_factor_code","variables":{"otpCode":"1"}}""", body);
    }
}

/// <summary>Collects log entries for assertions.</summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new ListLogger(this);

    public ILogger<T> CreateLogger<T>() => new Logger<T>(new SingleProviderFactory(this));

    public void Dispose() { }

    private sealed class SingleProviderFactory(ILoggerProvider provider) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider p) { }
        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);
        public void Dispose() { }
    }

    private sealed class ListLogger(ListLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Entries) owner.Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

internal static class ServiceProviderHttpExtensions
{
    public static HttpClient GetHttpClient(this IServiceProvider services) =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<IHttpClientFactory>(services)
            .CreateClient(OutboxProcessor.HttpClientName);
}
