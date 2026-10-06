using System.Net;
using System.Text.Json;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Backoffice.Email;

public class EmailRawOutboxHandlerTests
{
    [Fact]
    public async Task Queued_IsSentWithTheServicesId()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.TwoFactorCode);
        email.Host.Service.Enqueue(HttpStatusCode.Accepted,
            """{"id":"8b0e3c1c-4d8a-4a53-9d1a-0c6f7c3d2a10","status":"queued","send_at":null,"suppression_reason":null}""");

        await email.Host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await email.Host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Sent, item.Status);
        Assert.Equal("8b0e3c1c-4d8a-4a53-9d1a-0c6f7c3d2a10", item.RemoteId);
        Assert.Null(item.PayloadProtected);

        var (request, body) = Assert.Single(email.Host.Service.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/v1/emails", request.RequestUri!.AbsolutePath);
        Assert.Equal(id.ToString(), request.Headers.GetValues(OutboxRequest.IdempotencyKeyHeader).Single());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("jan@example.com", json.RootElement.GetProperty("to").GetString());
        Assert.True(json.RootElement.GetProperty("critical").GetBoolean());
        Assert.Equal("two_factor_code", json.RootElement.GetProperty("tags").GetProperty("template").GetString());
        Assert.Empty(email.DirectSends);
    }

    [Fact]
    public async Task RefusedWithFieldErrors_RecordsWhichFieldTheServiceRefused()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.PaymentFailed);
        email.Host.Service.Enqueue(
            HttpStatusCode.UnprocessableEntity,
            """
            {"type":"about:blank","title":"Unprocessable Entity","status":422,"code":"email_invalid",
             "detail":"One or more e-mails were refused; nothing was queued.",
             "errors":[{"path":"0.from","message":"from_domain_unknown"}]}
            """);

        await email.Host.Processor.DispatchDueAsync(CancellationToken.None);

        // Without the errors this reads as a bare "HTTP 422 email_invalid", which does not
        // say that the sending domain is the problem.
        var item = await email.Host.ItemAsync(id);
        Assert.Equal("HTTP 422 email_invalid (0.from: from_domain_unknown)", item.LastError);
    }

    [Fact]
    public async Task RefusedWithManyFieldErrors_KeepsTheErrorShort()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.PaymentFailed);
        email.Host.Service.Enqueue(
            HttpStatusCode.UnprocessableEntity,
            """
            {"status":422,"code":"email_invalid","errors":[
              {"path":"0.from","message":"address_invalid"},
              {"path":"0.to","message":"address_invalid"},
              {"path":"0.subject","message":"subject_required"},
              {"path":"0.html","message":"body_required"}]}
            """);

        await email.Host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await email.Host.ItemAsync(id);
        Assert.Equal(
            "HTTP 422 email_invalid (0.from: address_invalid, 0.to: address_invalid, 0.subject: subject_required)",
            item.LastError);
    }

    [Fact]
    public async Task RefusedWithoutFieldErrors_KeepsTheCodeOnItsOwn()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.PaymentFailed);
        email.Host.Service.Enqueue(HttpStatusCode.Conflict, """{"status":409,"code":"email_not_configured"}""");

        await email.Host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await email.Host.ItemAsync(id);
        Assert.Equal("HTTP 409 email_not_configured", item.LastError);
    }

    [Fact]
    public async Task SuppressedIn202_IsDeadWithoutPayload()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.PaymentFailed);
        email.Host.Service.Enqueue(HttpStatusCode.Accepted,
            """{"id":"res_9","status":"suppressed","send_at":null,"suppression_reason":"hard_bounce"}""");

        await email.Host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await email.Host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Dead, item.Status);
        Assert.Equal("res_9", item.RemoteId);
        Assert.Null(item.PayloadProtected);
        Assert.Equal("Recipient suppressed", item.LastError);
    }

    [Fact]
    public async Task SuppressedRecipient422_IsDead()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.PaymentFailed);
        email.Host.Service.EnqueueProblem(HttpStatusCode.UnprocessableEntity, RetryPolicy.SuppressedRecipient);

        await email.Host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await email.Host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Dead, item.Status);
        Assert.Null(item.PayloadProtected);
    }

    [Fact]
    public async Task ServerError_IsRetried()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.PaymentFailed);
        email.Host.Service.Enqueue(HttpStatusCode.ServiceUnavailable);

        await email.Host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await email.Host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Pending, item.Status);
        Assert.Equal(1, item.Attempts);
        Assert.NotNull(item.PayloadProtected);
    }

    [Fact]
    public async Task PausedEmailKinds_AreNotClaimed()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.PaymentFailed);
        var services = email.Host.Services;
        var processor = new OutboxProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetServices<IOutboxHandler>(),
            services.GetRequiredService<OutboxPayloadProtector>(),
            email.Breaker,
            email.Host.Time,
            NullLogger<OutboxProcessor>.Instance,
            new OutboxKindFilter(["email."]));

        Assert.Equal(0, await processor.DispatchDueAsync(CancellationToken.None));

        var item = await email.Host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Pending, item.Status);
        Assert.Equal(0, item.Attempts);
        Assert.Empty(email.Host.Service.Requests);
    }
}
