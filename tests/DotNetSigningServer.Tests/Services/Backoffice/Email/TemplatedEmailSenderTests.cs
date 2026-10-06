using DotNetSigningServer.Services.Email;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services.Backoffice.Email;

public class TemplatedEmailSenderTests
{
    private static readonly IReadOnlyDictionary<string, string?> TwoFactor = EmailTemplateVariables.TwoFactorCode("123456", 10);

    private static readonly IReadOnlyDictionary<string, string?> Recharge =
        EmailTemplateVariables.AutoRechargeSuccess("100", "12.00", "EUR", "110", "https://app.example.com/Billing");

    [Fact]
    public async Task EmptyTemplateKeys_RendersLocallyAsRawEmail()
    {
        using var host = new TemplatedEmailTestHost("On");

        Assert.True(await host.SendAsync(EmailTemplateId.AutoRechargeSuccess, Recharge));

        var item = Assert.Single(await host.ItemsAsync());
        Assert.Equal("email.raw", item.Kind);
        using var payload = host.Payload(item);
        Assert.Contains("110", payload.RootElement.GetProperty("html").GetString());
        Assert.False(payload.RootElement.TryGetProperty("template", out _));
    }

    [Fact]
    public async Task WildcardTemplates_QueuesServiceTemplateForAnUnlistedKey()
    {
        using var host = new TemplatedEmailTestHost("On");
        host.Options.CurrentValue.Email.Templates = "*";

        Assert.True(await host.SendAsync(EmailTemplateId.AutoRechargeSuccess, Recharge));

        var item = Assert.Single(await host.ItemsAsync());
        Assert.Equal(TemplatedEmailSender.OutboxKind, item.Kind);
    }

    [Fact]
    public async Task WildcardRemoved_GoesBackToLocalRenderingWithoutARestart()
    {
        using var host = new TemplatedEmailTestHost("On");
        host.Options.CurrentValue.Email.Templates = "*";
        Assert.True(await host.SendAsync(EmailTemplateId.AutoRechargeSuccess, Recharge));

        host.Options.CurrentValue.Email.Templates = null;
        Assert.True(await host.SendAsync(EmailTemplateId.AutoRechargeSuccess, Recharge));

        var kinds = (await host.ItemsAsync()).Select(i => i.Kind).ToList();
        Assert.Equal(new List<string> { TemplatedEmailSender.OutboxKind, "email.raw" }, kinds);
    }

    [Fact]
    public async Task ListedKeyWithEmailOn_QueuesServiceTemplate()
    {
        using var host = new TemplatedEmailTestHost("On", EmailTemplateId.AutoRechargeSuccess);
        var userId = Guid.NewGuid();

        Assert.True(await host.SendAsync(EmailTemplateId.AutoRechargeSuccess, Recharge, userId: userId));

        var item = Assert.Single(await host.ItemsAsync());
        Assert.Equal(TemplatedEmailSender.OutboxKind, item.Kind);
        Assert.False(item.Critical);
        Assert.Equal($"user:{userId}", item.SubjectRef);
        using var payload = host.Payload(item);
        var root = payload.RootElement;
        Assert.Equal("jan@example.com", root.GetProperty("to").GetString());
        Assert.Equal("auto_recharge_success", root.GetProperty("template").GetString());
        Assert.Equal("cs", root.GetProperty("locale").GetString());
        Assert.Equal("110", root.GetProperty("variables").GetProperty("newBalance").GetString());
        Assert.Equal("auto_recharge_success", root.GetProperty("tags").GetProperty("template").GetString());
        Assert.Equal(userId.ToString("D"), root.GetProperty("tags").GetProperty("user_id").GetString());
        Assert.Equal(EmailTestHost.From, root.GetProperty("from").GetString());
        foreach (var local in new[] { "subject", "html", "text", "fallback", "critical", "category" })
        {
            Assert.False(root.TryGetProperty(local, out _), $"{local} must not be sent in template mode");
        }
    }

    [Fact]
    public async Task CriticalTemplate_KeepsTheLocalRenderingForBreakGlass()
    {
        using var host = new TemplatedEmailTestHost("On", EmailTemplateId.TwoFactorCode);

        await host.SendAsync(EmailTemplateId.TwoFactorCode, TwoFactor, locale: "en");

        var item = Assert.Single(await host.ItemsAsync());
        Assert.Equal(TemplatedEmailSender.OutboxKind, item.Kind);
        Assert.True(item.Critical);
        using var payload = host.Payload(item);
        var fallback = payload.RootElement.GetProperty("fallback");
        Assert.Equal("Your Performance4PDF sign-in code", fallback.GetProperty("subject").GetString());
        Assert.Contains("123456", fallback.GetProperty("html").GetString());
        Assert.Equal("123456", payload.RootElement.GetProperty("variables").GetProperty("otpCode").GetString());
    }

    [Fact]
    public async Task OtherKeysStayLocal_WhenOnlySomeAreListed()
    {
        using var host = new TemplatedEmailTestHost("On", EmailTemplateId.AutoRechargeSuccess);

        await host.SendAsync(EmailTemplateId.TwoFactorCode, TwoFactor);

        Assert.Equal("email.raw", Assert.Single(await host.ItemsAsync()).Kind);
    }

    [Fact]
    public async Task RemovingTheKey_SwitchesBackToLocalRenderingWithoutRestart()
    {
        using var host = new TemplatedEmailTestHost("On", EmailTemplateId.AutoRechargeSuccess);
        await host.SendAsync(EmailTemplateId.AutoRechargeSuccess, Recharge);

        host.Options.CurrentValue = TemplatedEmailTestHost.OptionsFor("On");
        host.Host.Time.Advance(TimeSpan.FromSeconds(1));
        await host.SendAsync(EmailTemplateId.AutoRechargeSuccess, Recharge);

        Assert.Equal(["email.template", "email.raw"], (await host.ItemsAsync()).Select(i => i.Kind));
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("Shadow")]
    public async Task EmailNotOn_SendsLocallyEvenForListedKeys(string mode)
    {
        using var host = new TemplatedEmailTestHost(mode, EmailTemplateId.AutoRechargeSuccess);

        Assert.False(await host.SendAsync(EmailTemplateId.AutoRechargeSuccess, Recharge, locale: "en"));

        Assert.Empty(await host.ItemsAsync());
        var sent = Assert.Single(host.Direct.Sent);
        Assert.Equal("jan@example.com", sent.To);
        Assert.Contains("110", sent.Html);
        Assert.Equal(EmailTemplateId.AutoRechargeSuccess, sent.Options?.TemplateId);
    }

    [Fact]
    public async Task SendAsync_OnTemplatePath_QueuesAndSaves()
    {
        using var host = new TemplatedEmailTestHost("On", EmailTemplateId.AutoRechargeSuccess);

        using (var scope = host.Host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITemplatedEmailSender>()
                .SendAsync(EmailTemplateId.AutoRechargeSuccess, "jan@example.com", "cs", Recharge);
        }

        Assert.Equal(TemplatedEmailSender.OutboxKind, Assert.Single(await host.ItemsAsync()).Kind);
    }

    [Fact]
    public void RouteFor_FollowsModeAndKeys()
    {
        using var host = new TemplatedEmailTestHost("On", EmailTemplateId.PaymentFailed, " password_reset ");
        using var scope = host.Host.Services.CreateScope();
        var sender = (TemplatedEmailSender)scope.ServiceProvider.GetRequiredService<ITemplatedEmailSender>();

        Assert.Equal(TemplatedEmailRoute.ServiceTemplate, sender.RouteFor(EmailTemplateId.PaymentFailed));
        Assert.Equal(TemplatedEmailRoute.ServiceTemplate, sender.RouteFor(EmailTemplateId.PasswordReset));
        Assert.Equal(TemplatedEmailRoute.Local, sender.RouteFor(EmailTemplateId.EmailVerification));

        var disabled = TemplatedEmailTestHost.OptionsFor("On", EmailTemplateId.PaymentFailed);
        disabled.DisabledReason = DotNetSigningServer.Services.Backoffice.BackofficeDisabledReason.PrivateServer;
        host.Options.CurrentValue = disabled;
        Assert.Equal(TemplatedEmailRoute.Local, sender.RouteFor(EmailTemplateId.PaymentFailed));
    }

    [Theory]
    [InlineData("cs", "cs")]
    [InlineData("cs-CZ", "cs")]
    [InlineData("EN", "en")]
    [InlineData("de", "en")]
    [InlineData("es-ES", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void ServiceLocale_IsCsOrEn(string? locale, string expected)
    {
        Assert.Equal(expected, EmailTemplateLocales.ForService(locale));
    }

    [Fact]
    public void BuildPayload_TurnsMissingValuesIntoEmptyStrings()
    {
        var payload = TemplatedEmailSender.BuildPayload(
            EmailTemplateId.EmailVerification, "jan@example.com", "de",
            new Dictionary<string, string?> { ["verificationUrl"] = null }, options: null);

        Assert.Equal("", payload.Variables["verificationUrl"]);
        Assert.Equal("en", payload.Locale);
        Assert.Null(payload.Fallback);
    }
}
