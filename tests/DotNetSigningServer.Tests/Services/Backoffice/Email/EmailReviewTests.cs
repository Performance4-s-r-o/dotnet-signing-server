using System.Net;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static DotNetSigningServer.Tests.Services.Backoffice.BackofficeRegistrationTests;

namespace DotNetSigningServer.Tests.Services.Backoffice.Email;

/// <summary>Outage reporting and the expiry of critical e-mail (H1).</summary>
public class CriticalEmailExpiryTests
{
    [Fact]
    public async Task CriticalEmail_ExpiresAfterAnHour_WithoutPayload()
    {
        using var email = new EmailTestHost();
        var id = await email.SendAsync(EmailTemplateId.TwoFactorCode);
        email.Host.Service.EnqueueProblem(HttpStatusCode.BadGateway, "bad_gateway");
        await email.Host.Processor.DispatchDueAsync(CancellationToken.None);
        Assert.Equal(BackofficeOutboxStatus.Pending, (await email.Host.ItemAsync(id)).Status);

        email.Host.Time.Advance(TimeSpan.FromMinutes(61));
        email.Host.Service.EnqueueProblem(HttpStatusCode.BadGateway, "bad_gateway");
        await email.Host.Processor.DispatchDueAsync(CancellationToken.None);

        var item = await email.Host.ItemAsync(id);
        Assert.Equal(BackofficeOutboxStatus.Dead, item.Status);
        Assert.StartsWith("Expired before delivery", item.LastError);
        Assert.Null(item.PayloadProtected);
    }

    [Fact]
    public async Task NonCriticalEmail_KeepsTheUsualGiveUpTime()
    {
        using var email = new EmailTestHost();
        var critical = await email.SendAsync(EmailTemplateId.TwoFactorCode);
        var plain = await email.SendAsync(EmailTemplateId.PaymentFailed);

        Assert.Equal(EmailRawOutboxHandler.CriticalExpiry, email.Host.Processor.GiveUpAfterFor(await email.Host.ItemAsync(critical)));
        Assert.Equal(TimeSpan.FromHours(20), email.Host.Processor.GiveUpAfterFor(await email.Host.ItemAsync(plain)));
    }

    [Fact]
    public async Task EmailNotAcceptedForFiveMinutes_IsAnAlert()
    {
        using var email = new EmailTestHost();
        var item = await email.Host.ItemAsync(await email.SendAsync(EmailTemplateId.PaymentFailed));

        Assert.False(email.Host.Processor.IsOverdue(item, item.CreatedAt + TimeSpan.FromMinutes(4)));
        Assert.True(email.Host.Processor.IsOverdue(item, item.CreatedAt + EmailRawOutboxHandler.MailAlertAfter));
    }
}

/// <summary>From / Reply-To (H2), the development sender (H3, M1) and the on-prem gateway.</summary>
public class EmailSenderAndGatewayTests
{
    private static P4BackofficeProductOptions Options(Action<P4BackofficeProductOptions>? change = null)
    {
        var o = new P4BackofficeProductOptions { BaseUrl = "https://backoffice.example.com", SecretKey = "p4sk_test_abc", Environment = "test" };
        change?.Invoke(o);
        return o;
    }

    [Theory]
    [InlineData("")]
    [InlineData("{{project.DOTNET_P4_BACKOFFICE_EMAIL_FROM}}")]
    [InlineData(" {{ project.X }} ")]
    public void UnsetFromAndReplyTo_AreNotSent(string raw)
    {
        var o = Options(x => { x.Email.From = raw; x.Email.ReplyTo = raw; });

        Assert.Null(o.Email.EffectiveFrom);
        Assert.Null(o.Email.EffectiveReplyTo);
        Assert.Empty(BackofficeOptionsValidator.Validate(o, requireEmail: true));
    }

    [Fact]
    public void FromThatIsNoAddress_IsRefused()
    {
        var o = Options(x => { x.Email.From = "not an address"; x.Email.ReplyTo = "nope"; });

        var problems = BackofficeOptionsValidator.Validate(o, requireEmail: true);
        Assert.Contains(problems, p => p.Contains("P4_BACKOFFICE_EMAIL_FROM"));
        Assert.Contains(problems, p => p.Contains("P4_BACKOFFICE_EMAIL_REPLY_TO"));
        Assert.Empty(BackofficeOptionsValidator.Validate(Options(x => x.Email.From = "Performance4PDF <noreply@send.example.com>")));
    }

    [Fact]
    public void ALoneUrlOrKey_DoesNotSwitchEmailOn()
    {
        Assert.Equal(BackofficeMode.Off, new P4BackofficeProductOptions { BaseUrl = "https://x.example.com" }.ModeFor(BackofficeModule.Email));
        Assert.Equal(BackofficeMode.Off, new P4BackofficeProductOptions { SecretKey = "p4sk_test_abc" }.ModeFor(BackofficeModule.Email));
        Assert.Equal(BackofficeMode.On, Options().ModeFor(BackofficeModule.Email));
    }

    private static LoggingEmailSender Logging(string environment, Action<P4BackofficeProductOptions>? change, out ListLoggerProvider log)
    {
        log = new ListLoggerProvider();
        return new LoggingEmailSender(
            new TestHostEnvironment { EnvironmentName = environment },
            Microsoft.Extensions.Options.Options.Create(Options(change)),
            log.CreateLogger<LoggingEmailSender>());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task LoggingSender_OutsideDevelopment_Throws(string environment)
    {
        var sender = Logging(environment, null, out var log);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync("jan@example.com", "Code", "<p>123456</p>"));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("jan@example.com") || e.Message.Contains("123456"));
    }

    [Fact]
    public async Task LoggingSender_InDevelopment_LogsAMaskedRecipientAndNoBody()
    {
        var sender = Logging("Development", null, out var log);

        await sender.SendAsync("jan@example.com", "Code", "<p>123456</p>");

        Assert.Contains(log.Entries, e => e.Message.Contains("j***@example.com") && e.Message.Contains("Code"));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("jan@example.com") || e.Message.Contains("123456"));
    }

    [Fact]
    public async Task LoggingSender_BodyOnlyWithLogBodiesInDevelopment()
    {
        var sender = Logging("Development", o => o.Email.LogBodies = true, out var log);

        await sender.SendAsync("jan@example.com", "Code", "<p>123456</p>");

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("123456"));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("jan@example.com"));
    }

    private static P4BackofficeProductOptions Gateway(Action<P4BackofficeProductOptions>? change = null)
    {
        var o = new P4BackofficeProductOptions
        {
            DisabledReason = BackofficeDisabledReason.PrivateServer,
            OnPremGateway = "true",
            BaseUrl = "http://notify-gateway:8090",
            SecretKey = "gw_key",
        };
        change?.Invoke(o);
        return o;
    }

    [Fact]
    public void Gateway_OnAPrivateServer_TurnsOnlyEmailOn()
    {
        var o = Gateway();

        Assert.Equal(BackofficeMode.On, o.ModeFor(BackofficeModule.Email));
        Assert.All(Enum.GetValues<BackofficeModule>().Where(m => m != BackofficeModule.Email), m => Assert.Equal(BackofficeMode.Off, o.ModeFor(m)));
        Assert.True(o.AnyEnabled);
        Assert.False(o.AnyEnabledBeyondEmail);
        Assert.False(o.EventsEnabled);
        Assert.Empty(BackofficeOptionsValidator.Validate(o, requireEmail: true, requireBilling: false));
    }

    [Theory]
    [InlineData("True")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData(null)]
    public void Gateway_IsOnOnlyForExactlyTrue(string? value)
    {
        var o = Gateway(x => x.OnPremGateway = value);

        Assert.Equal(BackofficeMode.Off, o.ModeFor(BackofficeModule.Email));
        Assert.False(o.GatewayOnly);
    }

    [Fact]
    public void Gateway_WithoutAHostedServer_IsIgnored()
    {
        var o = Gateway(x => x.DisabledReason = BackofficeDisabledReason.None);

        Assert.False(o.GatewayOnly);
    }

    [Fact]
    public void Gateway_Validation()
    {
        Assert.Contains(BackofficeOptionsValidator.Validate(Gateway(x => x.Email.TemplateKeys = ["password_reset"])), p => p.Contains("gateway knows no service templates"));
        Assert.Contains(BackofficeOptionsValidator.Validate(Gateway(x => x.Email.Templates = "*")), p => p.Contains("gateway knows no service templates"));
        Assert.Contains(BackofficeOptionsValidator.Validate(Gateway(x => x.SecretKey = null)), p => p.Contains("SecretKey"));
        Assert.Contains(BackofficeOptionsValidator.Validate(Gateway(x => x.BaseUrl = "notaurl")), p => p.Contains("gateway's absolute"));
        Assert.Contains(BackofficeOptionsValidator.Validate(Gateway(x => x.BaseUrl = null), requireEmail: true), p => p.Contains("BaseUrl"));
        // Without a gateway a private server stays exempt.
        Assert.Empty(BackofficeOptionsValidator.Validate(
            new P4BackofficeProductOptions { DisabledReason = BackofficeDisabledReason.PrivateServer }, requireEmail: true));
    }

    private static Dictionary<string, string?> Connected() => new()
    {
        ["P4Backoffice:BaseUrl"] = "https://backoffice.example.com",
        ["P4Backoffice:SecretKey"] = "p4sk_test_abc",
        ["P4Backoffice:Environment"] = "test",
    };

    [Fact]
    public void Gateway_RegistersTheDispatcherButNoInboxNoPollingNoErasure()
    {
        var services = Register(new()
        {
            ["P4Backoffice:OnPremGateway"] = "true",
            ["P4Backoffice:BaseUrl"] = "http://notify-gateway:8090",
            ["P4Backoffice:SecretKey"] = "gw_key",
        }, privateServer: true);

        Assert.False(AnyInboxWorker(services));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(BackofficeOutboxDispatcher));
        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(ErasureRequestedHandler));
        Assert.Contains(services, d => d.ServiceType == typeof(IEmailSender) && d.ImplementationFactory != null);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IEmailSender) && d.ImplementationType == typeof(LoggingEmailSender));
    }

    [Fact]
    public void PrivateServerWithoutGateway_RegistersNoDispatcher()
    {
        var services = Register(Connected(), privateServer: true);

        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(BackofficeOutboxDispatcher));
    }

    [Fact]
    public void EmailAlone_RunsTheInboxForBouncesButNotTheErasureQueue()
    {
        var services = Register(Connected());

        Assert.True(AnyInboxWorker(services));
        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(ErasureRequestedHandler));
    }

    [Fact]
    public void AnotherModule_AddsTheErasureQueue()
    {
        var settings = Connected();
        settings["P4Backoffice:Modules:Docs"] = "On";

        Assert.Contains(Register(settings), d => d.ImplementationType == typeof(ErasureRequestedHandler));
    }
}
