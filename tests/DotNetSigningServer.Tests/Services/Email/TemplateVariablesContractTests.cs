using System.Reflection;
using System.Text.RegularExpressions;
using DotNetSigningServer.Services.Email;

namespace DotNetSigningServer.Tests.Services.Email;

/// <summary>
/// Pure contract: what every caller sends per template equals the variables of the service's
/// templates (camelCase strings, all required) and the placeholders of the local templates.
/// </summary>
public class TemplateVariablesContractTests
{
    /// <summary>The service's template schemas (published import of the 7 dotnet templates).</summary>
    public static readonly Dictionary<string, string[]> ServiceSchema = new()
    {
        ["email_verification"] = ["verificationUrl"],
        ["two_factor_code"] = ["otpCode", "expiryMinutes"],
        ["password_reset"] = ["resetUrl", "expiryMinutes"],
        ["payment_failed"] = ["paymentType", "amount", "currency", "failureReason", "billingUrl"],
        ["auto_recharge_success"] = ["quantity", "amount", "currency", "newBalance", "billingUrl"],
        ["auto_recharge_failed"] = ["quantity", "failureReason", "currentBalance", "billingUrl"],
        ["price_change_notice"] = ["daysNotice", "quantity", "oldPrice", "newPrice", "currency", "cancelUrl", "billingUrl"],
    };

    /// <summary>What each caller's builder produces (arguments are placeholders).</summary>
    public static readonly Dictionary<string, IReadOnlyDictionary<string, string?>> CallerVariables = new()
    {
        [EmailTemplateId.EmailVerification] = EmailTemplateVariables.EmailVerification("https://x/verify"),
        [EmailTemplateId.TwoFactorCode] = EmailTemplateVariables.TwoFactorCode("123456", 10),
        [EmailTemplateId.PasswordReset] = EmailTemplateVariables.PasswordReset("https://x/reset", 60),
        [EmailTemplateId.PaymentFailed] = EmailTemplateVariables.PaymentFailed("purchase", "1.00", "EUR", "declined", "https://x/Billing"),
        [EmailTemplateId.AutoRechargeSuccess] = EmailTemplateVariables.AutoRechargeSuccess("100", "1.00", "EUR", "110", "https://x/Billing"),
        [EmailTemplateId.AutoRechargeFailed] = EmailTemplateVariables.AutoRechargeFailed("100", "declined", "5", "https://x/Billing"),
        [EmailTemplateId.PriceChangeNotice] = EmailTemplateVariables.PriceChangeNotice("30", "100", "1.00", "2.00", "EUR", "https://x/cancel", "https://x/Billing"),
    };

    public static IEnumerable<object[]> TemplateKeys => ServiceSchema.Keys.Select(k => new object[] { k });

    [Fact]
    public void EveryTemplateId_IsCovered()
    {
        var ids = typeof(EmailTemplateId)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order();

        Assert.Equal(ids, ServiceSchema.Keys.Order());
        Assert.Equal(ids, EmailTemplateVariables.Names.Keys.Order());
        Assert.Equal(ids, CallerVariables.Keys.Order());
    }

    [Theory]
    [MemberData(nameof(TemplateKeys))]
    public void CallerVariables_MatchTheServiceSchema(string templateKey)
    {
        var expected = ServiceSchema[templateKey].Order();

        Assert.Equal(expected, CallerVariables[templateKey].Keys.Order());
        Assert.Equal(expected, EmailTemplateVariables.Names[templateKey].Order());
        Assert.All(CallerVariables[templateKey].Values, v => Assert.False(string.IsNullOrEmpty(v)));
    }

    [Theory]
    [MemberData(nameof(TemplateKeys))]
    public void LocalTemplates_UseTheSameVariables(string templateKey)
    {
        foreach (var locale in EmailTemplateLocales.Supported)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Resources", "EmailTemplates", locale, templateKey + ".html");
            var placeholders = Regex.Matches(File.ReadAllText(path), @"\{\{(?<name>[a-zA-Z0-9_]+)\}\}")
                .Select(m => m.Groups["name"].Value)
                .Distinct()
                .Order();

            Assert.Equal(ServiceSchema[templateKey].Order(), placeholders);
        }
    }

    [Fact]
    public void ServiceTemplates_AreTemplateKeysTheServiceAccepts()
    {
        Assert.All(ServiceSchema.Keys, key => Assert.Matches("^[a-z][a-z0-9_]{1,59}$", key));
    }
}
