using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services.Backoffice.Email;

public class BackofficeOutboxEmailSenderTests
{
    private static JsonElement Payload(EmailTestHost email, BackofficeOutboxItem item)
    {
        var protector = email.Host.Services.GetRequiredService<OutboxPayloadProtector>();
        return JsonDocument.Parse(protector.Unprotect(item.PayloadProtected!)).RootElement.Clone();
    }

    [Fact]
    public async Task SendAsync_QueuesAnEncryptedEmailRawItemAndSaves()
    {
        using var email = new EmailTestHost();
        var userId = Guid.NewGuid();

        var id = await email.SendAsync(EmailTemplateId.AutoRechargeSuccess, userId: userId);

        var item = await email.Host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxEmailSender.OutboxKind, item.Kind);
        Assert.Equal(BackofficeOutboxStatus.Pending, item.Status);
        Assert.False(item.Critical);
        Assert.Equal($"user:{userId}", item.SubjectRef);
        // Nothing readable at rest: the code in the body is only in the encrypted payload.
        Assert.DoesNotContain("123456", item.PayloadProtected);
        Assert.DoesNotContain("jan@example.com", item.PayloadProtected);

        var payload = Payload(email, item);
        Assert.Equal("jan@example.com", payload.GetProperty("to").GetString());
        Assert.Equal("Subject auto_recharge_success", payload.GetProperty("subject").GetString());
        Assert.Equal("<p>code 123456</p>", payload.GetProperty("html").GetString());
        Assert.Equal(EmailTestHost.From, payload.GetProperty("from").GetString());
        Assert.False(payload.TryGetProperty("reply_to", out _));
        Assert.Equal("transactional", payload.GetProperty("category").GetString());
        Assert.False(payload.GetProperty("critical").GetBoolean());
        var tags = payload.GetProperty("tags");
        Assert.Equal("auto_recharge_success", tags.GetProperty("template").GetString());
        Assert.Equal("cs", tags.GetProperty("locale").GetString());
        Assert.Equal(userId.ToString("D"), tags.GetProperty("user_id").GetString());
    }

    [Theory]
    [InlineData(EmailTemplateId.TwoFactorCode, true)]
    [InlineData(EmailTemplateId.PasswordReset, true)]
    [InlineData(EmailTemplateId.EmailVerification, true)]
    [InlineData(EmailTemplateId.PaymentFailed, false)]
    [InlineData(EmailTemplateId.AutoRechargeFailed, false)]
    [InlineData(EmailTemplateId.AutoRechargeSuccess, false)]
    [InlineData(EmailTemplateId.PriceChangeNotice, false)]
    public async Task Critical_FollowsTheTemplate(string templateId, bool critical)
    {
        using var email = new EmailTestHost();

        var id = await email.SendAsync(templateId);

        var item = await email.Host.ItemAsync(id);
        Assert.Equal(critical, item.Critical);
        Assert.Equal(critical, Payload(email, item).GetProperty("critical").GetBoolean());
    }

    [Fact]
    public async Task Enqueue_DoesNotSave_TheCallersSaveChangesCommitsIt()
    {
        using var email = new EmailTestHost();
        using var scope = email.Host.Services.CreateScope();
        IEmailSender sender = scope.ServiceProvider.GetRequiredService<BackofficeOutboxEmailSender>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.True(sender.TryEnqueue("jan@example.com", "s", "<p>h</p>", new EmailSendOptions(EmailTemplateId.TwoFactorCode, "en")));
        Assert.Equal(0, await email.Host.WithDbAsync(d => d.BackofficeOutboxItems.CountAsync()));

        await db.SaveChangesAsync();
        Assert.Equal(1, await email.Host.WithDbAsync(d => d.BackofficeOutboxItems.CountAsync()));
    }

    [Fact]
    public void TryEnqueue_OnADirectSender_ReturnsFalse()
    {
        IEmailSender direct = new DirectSender();
        Assert.False(direct.TryEnqueue("jan@example.com", "s", "h", null));
    }

    [Fact]
    public void Tags_KeepOnlyTheServicesCharacters()
    {
        var tags = BackofficeOutboxEmailSender.Tags(new EmailSendOptions("two_factor_code", " pt-BR ", null));

        Assert.Equal("two_factor_code", tags["template"]);
        Assert.Equal("pt-BR", tags["locale"]);
        Assert.False(tags.ContainsKey("user_id"));
        Assert.Empty(BackofficeOutboxEmailSender.Tags(new EmailSendOptions("ü", "", null)));
        Assert.Empty(BackofficeOutboxEmailSender.Tags(null));
    }

    [Fact]
    public void CriticalEmails_AreTheThreeAccountMessages()
    {
        Assert.Equal(
            new[] { EmailTemplateId.EmailVerification, EmailTemplateId.PasswordReset, EmailTemplateId.TwoFactorCode },
            CriticalEmails.TemplateIds.OrderBy(t => t, StringComparer.Ordinal));
        Assert.False(CriticalEmails.IsCritical(null));
        Assert.True(new EmailSendOptions(EmailTemplateId.PaymentFailed, "en", Critical: true).IsCritical);
    }

    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("cs", "cs")]
    public void BackgroundEmails_UseTheLanguageOfTheLastSignIn(string? stored, string expected)
    {
        Assert.Equal(expected, new User { Locale = stored }.EmailLocale);
    }

    private sealed class DirectSender : IEmailSender
    {
        public Task SendAsync(string toEmail, string subject, string htmlBody) => Task.CompletedTask;
    }
}
