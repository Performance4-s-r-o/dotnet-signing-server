using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Email;

namespace DotNetSigningServer.Tests.Options;

/// <summary>
/// Which templates the service renders: <c>TemplateKeys</c> from configuration files and
/// <c>Templates</c> from a single environment value (<c>P4_BACKOFFICE_TEMPLATES</c>).
/// </summary>
public class BackofficeEmailOptionsTests
{
    private static P4BackofficeProductOptions.EmailOptions Options(string? templates = null, params string[] keys) =>
        new() { TemplateKeys = keys.ToList(), Templates = templates };

    [Fact]
    public void NothingConfigured_SelectsNothing()
    {
        var options = Options();

        Assert.False(options.Selects(EmailTemplateId.EmailVerification));
        Assert.Empty(options.SelectedKeys());
    }

    [Fact]
    public void Wildcard_SelectsEveryTemplate()
    {
        var options = Options("*");

        Assert.All(
            new[]
            {
                EmailTemplateId.EmailVerification, EmailTemplateId.PasswordReset, EmailTemplateId.TwoFactorCode,
                EmailTemplateId.PaymentFailed, EmailTemplateId.AutoRechargeFailed, EmailTemplateId.AutoRechargeSuccess,
                EmailTemplateId.PriceChangeNotice,
            },
            key => Assert.True(options.Selects(key)));

        // Including one the service does not know: the send fails loudly instead of
        // silently falling back to the local rendering.
        Assert.True(options.Selects("not_a_template"));
    }

    [Theory]
    [InlineData("email_verification,password_reset")]
    [InlineData("email_verification password_reset")]
    [InlineData(" email_verification ,  password_reset ")]
    [InlineData("email_verification;password_reset")]
    [InlineData("email_verification\npassword_reset")]
    public void Templates_AcceptsCommasAndWhitespace(string value)
    {
        var options = Options(value);

        Assert.True(options.Selects(EmailTemplateId.EmailVerification));
        Assert.True(options.Selects(EmailTemplateId.PasswordReset));
        Assert.False(options.Selects(EmailTemplateId.TwoFactorCode));
    }

    [Fact]
    public void Templates_AddToTemplateKeysRatherThanReplacingThem()
    {
        var options = Options(EmailTemplateId.PasswordReset, EmailTemplateId.EmailVerification);

        Assert.True(options.Selects(EmailTemplateId.EmailVerification));
        Assert.True(options.Selects(EmailTemplateId.PasswordReset));
    }

    [Fact]
    public void UnsetOrBlankTemplates_ChangeNothing()
    {
        Assert.False(Options("   ").Selects(EmailTemplateId.EmailVerification));
        Assert.True(Options("   ", EmailTemplateId.EmailVerification).Selects(EmailTemplateId.EmailVerification));
    }

    [Fact]
    public void KeysAreMatchedExactly()
    {
        var options = Options(EmailTemplateId.EmailVerification);

        Assert.False(options.Selects("EMAIL_VERIFICATION"));
        Assert.False(options.Selects("email_verification_2"));
        Assert.False(options.Selects("verification"));
    }
}
