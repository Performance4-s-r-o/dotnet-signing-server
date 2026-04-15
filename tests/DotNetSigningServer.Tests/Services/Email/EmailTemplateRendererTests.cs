using DotNetSigningServer.Services.Email;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DotNetSigningServer.Tests.Services.Email;

public class EmailTemplateRendererTests
{
    private static EmailTemplateRenderer CreateRenderer(string? contentRoot = null)
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(contentRoot ?? AppContext.BaseDirectory);
        env.SetupGet(e => e.EnvironmentName).Returns("Testing");
        env.SetupGet(e => e.ApplicationName).Returns("DotNetSigningServer.Tests");
        return new EmailTemplateRenderer(NullLogger<EmailTemplateRenderer>.Instance, env.Object);
    }

    [Fact]
    public void Render_EmailVerification_Cs_InterpolatesUrl()
    {
        var renderer = CreateRenderer();

        var result = renderer.Render(
            EmailTemplateId.EmailVerification,
            "cs",
            new Dictionary<string, string?>
            {
                ["verificationUrl"] = "https://app.p4pdf.cz/verify?t=abc",
            });

        Assert.Equal("Ověřte svůj email pro P4PDF", result.Subject);
        Assert.Contains("https://app.p4pdf.cz/verify?t=abc", result.HtmlBody);
        Assert.Contains("Aktivovat účet", result.HtmlBody); // cs CTA
        Assert.Contains("Tým P4PDF", result.HtmlBody);      // cs footer
        Assert.DoesNotContain("{{verificationUrl}}", result.HtmlBody);
        Assert.DoesNotContain("{{body}}", result.HtmlBody); // layout filled
    }

    [Fact]
    public void Render_PaymentFailed_En_IncludesDetails()
    {
        var renderer = CreateRenderer();

        var result = renderer.Render(
            EmailTemplateId.PaymentFailed,
            "en",
            new Dictionary<string, string?>
            {
                ["paymentType"] = "subscription renewal",
                ["amount"] = "29.00",
                ["currency"] = "EUR",
                ["failureReason"] = "Card declined",
                ["billingUrl"] = "https://app.p4pdf.cz/Billing",
            });

        Assert.Equal("Payment failed — action required", result.Subject);
        Assert.Contains("subscription renewal", result.HtmlBody);
        Assert.Contains("29.00", result.HtmlBody);
        Assert.Contains("EUR", result.HtmlBody);
        Assert.Contains("Card declined", result.HtmlBody);
        Assert.Contains("https://app.p4pdf.cz/Billing", result.HtmlBody);
        Assert.Contains("The P4PDF team", result.HtmlBody); // en footer
    }

    [Fact]
    public void Render_UnknownLocale_FallsBackToEnglish()
    {
        var renderer = CreateRenderer();

        var result = renderer.Render(
            EmailTemplateId.TwoFactorCode,
            "de", // no de/ folder; should fall back to en
            new Dictionary<string, string?>
            {
                ["otpCode"] = "123456",
                ["expiryMinutes"] = "10",
            });

        Assert.Contains("123456", result.HtmlBody);
        Assert.Contains("Sign-in code", result.HtmlBody); // en heading
    }

    [Fact]
    public void Render_LocaleWithRegion_StripsRegion()
    {
        var renderer = CreateRenderer();

        var result = renderer.Render(
            EmailTemplateId.PasswordReset,
            "en-US",
            new Dictionary<string, string?>
            {
                ["resetUrl"] = "https://app.p4pdf.cz/reset?t=x",
                ["expiryMinutes"] = "60",
            });

        Assert.Contains("Reset your P4PDF password", result.Subject);
        Assert.Contains("60 minutes", result.HtmlBody);
    }

    [Fact]
    public void Render_MissingPlaceholder_ReplacesWithEmptyString()
    {
        var renderer = CreateRenderer();

        // failureReason is expected but not provided
        var result = renderer.Render(
            EmailTemplateId.AutoRechargeFailed,
            "en",
            new Dictionary<string, string?>
            {
                ["quantity"] = "200",
                ["currentBalance"] = "5",
                ["billingUrl"] = "https://app.p4pdf.cz/Billing",
            });

        Assert.DoesNotContain("{{failureReason}}", result.HtmlBody);
        Assert.DoesNotContain("{{", result.HtmlBody); // no leftover placeholders
        Assert.Contains("200", result.HtmlBody);
    }

    [Fact]
    public void Render_UnknownTemplateId_Throws()
    {
        var renderer = CreateRenderer();

        Assert.Throws<FileNotFoundException>(() =>
            renderer.Render("nonexistent_template", "en", null));
    }

    [Fact]
    public void Render_CachesTemplate_SameFileLoadedOnce()
    {
        var renderer = CreateRenderer();

        var vars1 = new Dictionary<string, string?> { ["verificationUrl"] = "https://a" };
        var vars2 = new Dictionary<string, string?> { ["verificationUrl"] = "https://b" };

        var r1 = renderer.Render(EmailTemplateId.EmailVerification, "en", vars1);
        var r2 = renderer.Render(EmailTemplateId.EmailVerification, "en", vars2);

        // Cache returns a raw parsed template; vars must still be substituted per call
        Assert.Contains("https://a", r1.HtmlBody);
        Assert.Contains("https://b", r2.HtmlBody);
        Assert.DoesNotContain("https://b", r1.HtmlBody);
        Assert.DoesNotContain("https://a", r2.HtmlBody);
    }

    [Fact]
    public void Render_FooterVars_AreLocalizedInLayout()
    {
        var renderer = CreateRenderer();

        var cs = renderer.Render(
            EmailTemplateId.EmailVerification, "cs",
            new Dictionary<string, string?> { ["verificationUrl"] = "https://x" });
        var en = renderer.Render(
            EmailTemplateId.EmailVerification, "en",
            new Dictionary<string, string?> { ["verificationUrl"] = "https://x" });

        Assert.Contains("Tým P4PDF", cs.HtmlBody);
        Assert.Contains("support@performance4.cz", cs.HtmlBody);
        Assert.Contains("The P4PDF team", en.HtmlBody);
    }

    [Fact]
    public void Render_AllProductionTemplates_RenderInBothLocales()
    {
        var renderer = CreateRenderer();
        var allIds = new[]
        {
            EmailTemplateId.EmailVerification,
            EmailTemplateId.TwoFactorCode,
            EmailTemplateId.PasswordReset,
            EmailTemplateId.PaymentFailed,
            EmailTemplateId.AutoRechargeFailed,
            EmailTemplateId.PriceChangeNotice,
        };

        foreach (var id in allIds)
        foreach (var locale in new[] { "cs", "en" })
        {
            var result = renderer.Render(id, locale, new Dictionary<string, string?>());
            Assert.False(string.IsNullOrWhiteSpace(result.Subject), $"{id}/{locale} has empty subject");
            Assert.False(string.IsNullOrWhiteSpace(result.HtmlBody), $"{id}/{locale} has empty body");
            Assert.DoesNotContain("{{body}}", result.HtmlBody);
            Assert.DoesNotContain("{{footer_signature}}", result.HtmlBody);
        }
    }
}
