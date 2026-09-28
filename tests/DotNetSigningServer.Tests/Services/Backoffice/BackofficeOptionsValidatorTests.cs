using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;

namespace DotNetSigningServer.Tests.Services.Backoffice;

public class BackofficeOptionsValidatorTests
{
    private const string Url = "https://backoffice.example.com";
    private const string Key = "p4sk_test_abc";

    private static P4BackofficeProductOptions Options(
        string? mode = null,
        string? baseUrl = null,
        string? secretKey = null,
        string? webhookSecret = null,
        string? previousSecret = null,
        string? pricing = null,
        BackofficeDisabledReason reason = BackofficeDisabledReason.None) => new()
        {
            Mode = mode,
            BaseUrl = baseUrl,
            SecretKey = secretKey,
            Webhook = { Secret = webhookSecret, PreviousSecret = previousSecret },
            Modules = { Pricing = pricing },
            DisabledReason = reason,
        };

    public static TheoryData<string, P4BackofficeProductOptions, string?> Cases => new()
    {
        // name, options, expected fragment of the (single) problem; null = valid
        { "nothing configured", Options(), null },
        { "Off needs no key", Options(mode: "Off"), null },
        { "Off ignores a bad URL", Options(mode: "off", baseUrl: "not a url"), null },
        { "On with url and key", Options(mode: "On", baseUrl: Url, secretKey: Key), null },
        { "mode is case-insensitive", Options(mode: "sHaDoW", baseUrl: Url, secretKey: Key), null },
        { "live key", Options(mode: "On", baseUrl: Url, secretKey: "p4sk_live_x"), null },
        { "http on localhost", Options(mode: "On", baseUrl: "http://localhost:3000", secretKey: Key), null },
        { "http on 127.0.0.1", Options(mode: "On", baseUrl: "http://127.0.0.1:9", secretKey: Key), null },
        { "http on ::1", Options(mode: "On", baseUrl: "http://[::1]:3000", secretKey: Key), null },
        { "webhook secret", Options(webhookSecret: "whsec_c2VjcmV0LWtleS0xMjM0NTY3ODkwMTIzNDU2", previousSecret: "whsec_b2xkLXNlY3JldC1rZXktMDk4NzY1NDMyMQ=="), null },
        { "webhook secret of exactly 24 bytes", Options(webhookSecret: "whsec_" + Convert.ToBase64String(new byte[24])), null },
        { "webhook secret not base64", Options(webhookSecret: "whsec_abc"), "followed by base64" },
        { "webhook secret of 1 byte", Options(webhookSecret: "whsec_AA=="), "too short" },
        { "webhook secret of 23 bytes", Options(webhookSecret: "whsec_" + Convert.ToBase64String(new byte[23])), "too short" },
        { "previous secret too short", Options(previousSecret: "whsec_b2xkc2VjcmV0"), "P4Backoffice__Webhook__PreviousSecret" },
        { "unknown mode", Options(mode: "Enabled"), "P4Backoffice__Mode" },
        { "numeric mode", Options(mode: "2"), "P4Backoffice__Mode" },
        { "unknown module mode", Options(pricing: "yes"), "P4Backoffice__Modules__Pricing" },
        { "On without key", Options(mode: "On", baseUrl: Url), "P4Backoffice__SecretKey" },
        { "Shadow without key", Options(mode: "Shadow", baseUrl: Url), "P4Backoffice__SecretKey" },
        { "module On without key", Options(pricing: "On", baseUrl: Url), "P4Backoffice__SecretKey" },
        { "key without prefix", Options(mode: "On", baseUrl: Url, secretKey: "sk_live_x"), "must start with p4sk_" },
        { "On without url", Options(mode: "On", secretKey: Key), "P4Backoffice__BaseUrl" },
        { "relative url", Options(mode: "On", baseUrl: "/v1", secretKey: Key), "absolute https URL" },
        { "ftp url", Options(mode: "On", baseUrl: "ftp://example.com", secretKey: Key), "absolute https URL" },
        { "http off localhost", Options(mode: "On", baseUrl: "http://backoffice.example.com", secretKey: Key), "only for localhost" },
        { "bad webhook secret", Options(webhookSecret: "abc"), "P4Backoffice__Webhook__Secret" },
        { "bad previous secret", Options(previousSecret: "abc"), "P4Backoffice__Webhook__PreviousSecret" },
        { "PrivateServer skips key rules", Options(mode: "On", reason: BackofficeDisabledReason.PrivateServer), null },
        { "no SDK still validates", Options(mode: "On", baseUrl: Url, reason: BackofficeDisabledReason.SdkNotIncluded), "P4Backoffice__SecretKey" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Validate(string name, P4BackofficeProductOptions options, string? expected)
    {
        var problems = BackofficeOptionsValidator.Validate(options);

        if (expected is null)
        {
            Assert.True(problems.Count == 0, $"{name}: {string.Join(" | ", problems)}");
        }
        else
        {
            var problem = Assert.Single(problems);
            Assert.Contains(expected, problem);
        }
    }

    [Fact]
    public void ReportsEveryProblemAtOnce()
    {
        var problems = BackofficeOptionsValidator.Validate(Options(mode: "On", webhookSecret: "x"));

        Assert.Equal(3, problems.Count); // BaseUrl, SecretKey, Webhook:Secret
    }

    [Fact]
    public void NonPositivePollingInterval_IsRejected()
    {
        var options = Options();
        options.Polling.Interval = TimeSpan.Zero;

        Assert.Contains(BackofficeOptionsValidator.Validate(options), p => p.Contains("Polling__Interval"));
    }

    [Fact]
    public void PollingInterval_DefaultsToTheWebhookSetup()
    {
        var polling = new P4BackofficeProductOptions.PollingOptions();

        Assert.Equal(TimeSpan.FromMinutes(15), polling.EffectiveInterval(webhooksConfigured: true));
        Assert.Equal(TimeSpan.FromMinutes(2), polling.EffectiveInterval(webhooksConfigured: false));

        polling.Interval = TimeSpan.FromMinutes(5);
        Assert.Equal(TimeSpan.FromMinutes(5), polling.EffectiveInterval(webhooksConfigured: false));
    }

    [Theory]
    [InlineData("p4sk_test_x", true, true)]
    [InlineData("p4sk_test_x", false, false)]
    [InlineData("p4sk_live_x", true, false)]
    public void TestKeyInProduction(string key, bool production, bool expected)
    {
        var options = Options(mode: "On", baseUrl: Url, secretKey: key);

        Assert.Equal(expected, BackofficeOptionsValidator.IsTestKeyInProduction(options, production));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("p4sk_live_secretpart", "p4sk_live_…")]
    [InlineData("p4sk_test_secretpart", "p4sk_test_…")]
    [InlineData("p4sk_other", "p4sk_…")]
    [InlineData("whatever", "…")]
    public void KeyDisplay_NeverShowsTheSecretPart(string? key, string? expected)
    {
        Assert.Equal(expected, BackofficeOptionsValidator.KeyDisplay(key));
    }
}

public class P4BackofficeProductOptionsTests
{
    [Fact]
    public void Defaults_AreAllOff()
    {
        var options = new P4BackofficeProductOptions();

        Assert.All(Enum.GetValues<BackofficeModule>(), m => Assert.Equal(BackofficeMode.Off, options.ModeFor(m)));
        Assert.False(options.AnyEnabled);
    }

    [Fact]
    public void ModuleOverride_BeatsGlobalMode()
    {
        var options = new P4BackofficeProductOptions { Mode = "Shadow", Modules = { Pricing = "Off", Email = "on" } };

        Assert.Equal(BackofficeMode.Off, options.ModeFor(BackofficeModule.Pricing));
        Assert.Equal(BackofficeMode.On, options.ModeFor(BackofficeModule.Email));
        Assert.Equal(BackofficeMode.Shadow, options.ModeFor(BackofficeModule.Docs));
    }

    [Fact]
    public void EmptyModuleValue_InheritsGlobalMode()
    {
        // docker-compose passes unset module overrides as empty strings.
        var options = new P4BackofficeProductOptions { Mode = "On", Modules = { Docs = "" } };

        Assert.Equal(BackofficeMode.On, options.ModeFor(BackofficeModule.Docs));
    }

    [Theory]
    [InlineData(BackofficeDisabledReason.PrivateServer)]
    [InlineData(BackofficeDisabledReason.SdkNotIncluded)]
    public void DisabledReason_ForcesEveryModuleOff(BackofficeDisabledReason reason)
    {
        var options = new P4BackofficeProductOptions { Mode = "On", Modules = { Pricing = "On" }, DisabledReason = reason };

        Assert.All(Enum.GetValues<BackofficeModule>(), m => Assert.Equal(BackofficeMode.Off, options.ModeFor(m)));
        Assert.Equal(BackofficeMode.On, options.RequestedModeFor(BackofficeModule.Pricing));
        Assert.False(options.AnyEnabled);
        Assert.True(options.AnyRequested);
    }

    [Fact]
    public void ConsentDocuments_DefaultAndOverride()
    {
        var options = new P4BackofficeProductOptions();
        Assert.Equal(new[] { "terms", "dpa" }, options.Consents.EffectiveDocuments);
        Assert.Equal(new[] { "privacy" }, options.Consents.EffectiveAcknowledged);

        options.Consents.Documents = new[] { "terms" };
        Assert.Equal(new[] { "terms" }, options.Consents.EffectiveDocuments);
    }
}
