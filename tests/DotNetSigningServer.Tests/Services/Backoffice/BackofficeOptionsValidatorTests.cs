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
        BackofficeDisabledReason reason = BackofficeDisabledReason.None,
        string? environment = "test") => new()
        {
            Mode = mode,
            BaseUrl = baseUrl,
            SecretKey = secretKey,
            Environment = environment,
            Webhook = { Secret = webhookSecret, PreviousSecret = previousSecret },
            Modules = { Pricing = pricing },
            DisabledReason = reason,
        };

    public static TheoryData<string, P4BackofficeProductOptions, string?> Cases => new()
    {
        // name, options, expected fragment of the (single) problem; null = valid
        { "nothing configured", Options(), null },
        { "Off needs no key", Options(mode: "Off"), null },
        { "Off still checks a URL: e-mail uses it", Options(mode: "off", baseUrl: "not a url", secretKey: Key), "absolute https URL" },
        { "On with url and key", Options(mode: "On", baseUrl: Url, secretKey: Key), null },
        { "mode is case-insensitive", Options(mode: "oN", baseUrl: Url, secretKey: Key), null },
        { "live key", Options(mode: "On", baseUrl: Url, secretKey: "p4sk_live_x", environment: "live"), null },
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
        { "Shadow is no longer a mode", Options(mode: "Shadow", baseUrl: Url, secretKey: Key), "P4Backoffice__Mode" },
        { "module On without key", Options(pricing: "On", baseUrl: Url), "P4Backoffice__SecretKey" },
        { "key without prefix", Options(mode: "On", baseUrl: Url, secretKey: "sk_live_x"), "must start with p4sk_" },
        { "On without url", Options(mode: "On", secretKey: Key), "P4Backoffice__BaseUrl" },
        { "relative url", Options(mode: "On", baseUrl: "/v1", secretKey: Key), "absolute https URL" },
        { "ftp url", Options(mode: "On", baseUrl: "ftp://example.com", secretKey: Key), "absolute https URL" },
        { "http off localhost", Options(mode: "On", baseUrl: "http://backoffice.example.com", secretKey: Key), "only for localhost" },
        { "bad webhook secret", Options(webhookSecret: "abc"), "P4Backoffice__Webhook__Secret" },
        { "bad previous secret", Options(previousSecret: "abc"), "P4Backoffice__Webhook__PreviousSecret" },
        { "PrivateServer skips key rules", Options(mode: "On", reason: BackofficeDisabledReason.PrivateServer), null },
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

    [Theory]
    [InlineData(0, false)]
    [InlineData(24, false)] // the service forgets the Idempotency-Key after 24 h
    [InlineData(72, false)]
    [InlineData(1, true)]
    [InlineData(20, true)]
    [InlineData(23, true)]
    public void OutboxGiveUpAfter_MustStayUnderTheIdempotencyWindow(int hours, bool valid)
    {
        var options = Options();
        options.Outbox.GiveUpAfter = TimeSpan.FromHours(hours);

        var problems = BackofficeOptionsValidator.Validate(options);

        Assert.Equal(valid, !problems.Any(p => p.Contains("Outbox__GiveUpAfter")));
    }

    [Fact]
    public void OutboxGiveUpAfter_DefaultsTo20Hours()
    {
        Assert.Equal(TimeSpan.FromHours(20), new P4BackofficeProductOptions().Outbox.EffectiveGiveUpAfter);
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
    [InlineData("p4sk_live_x", "live", null)]
    [InlineData("p4sk_test_x", "test", null)]
    [InlineData("p4sk_test_x", " Test ", null)]
    [InlineData("p4sk_live_x", " TEST ", "live key")]
    [InlineData("p4sk_test_x", " LIVE ", "test key")]
    [InlineData("p4sk_test_x", "live", "test key")]
    [InlineData("p4sk_live_x", "test", "live key")]
    public void KeyKindMustMatchEnvironment(string key, string environment, string? problem)
    {
        var options = Options(mode: "On", baseUrl: Url, secretKey: key, environment: environment);

        var problems = BackofficeOptionsValidator.Validate(options);

        if (problem is null)
        {
            Assert.Empty(problems);
        }
        else
        {
            var single = Assert.Single(problems);
            Assert.Contains("P4Backoffice__SecretKey", single);
            Assert.Contains(problem, single);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Environment_IsRequiredOnceAModuleIsSwitchedOn(string? environment)
    {
        var options = Options(mode: "On", baseUrl: Url, secretKey: "p4sk_test_x", environment: environment);

        var single = Assert.Single(BackofficeOptionsValidator.Validate(options));
        Assert.Contains("P4_BACKOFFICE_ENV", single);
        Assert.Contains("required", single);
    }

    [Fact]
    public void Environment_MustBeLiveOrTest()
    {
        var options = Options(mode: "On", baseUrl: Url, secretKey: "p4sk_test_x", environment: "staging");

        var single = Assert.Single(BackofficeOptionsValidator.Validate(options));
        Assert.Contains("\"staging\"", single);
    }

    [Fact]
    public void Environment_IsCheckedForASingleModuleWithoutTheGlobalMode()
    {
        var options = Options(mode: "Off", baseUrl: Url, secretKey: "p4sk_live_x", environment: null);
        options.Modules.Docs = "On";

        Assert.Contains(BackofficeOptionsValidator.Validate(options), p => p.Contains("P4_BACKOFFICE_ENV"));
    }

    [Fact]
    public void KeyKind_IsNotCheckedOnAPrivateServer()
    {
        var options = Options(mode: "On", baseUrl: Url, secretKey: "p4sk_test_x", environment: null,
            reason: BackofficeDisabledReason.PrivateServer);

        Assert.Empty(BackofficeOptionsValidator.Validate(options));
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
        var options = new P4BackofficeProductOptions { Mode = "On", Modules = { Pricing = "Off" }, BaseUrl = "https://backoffice.example.com", SecretKey = "p4sk_test_abc" };

        Assert.Equal(BackofficeMode.Off, options.ModeFor(BackofficeModule.Pricing));
        Assert.Equal(BackofficeMode.On, options.ModeFor(BackofficeModule.Email));
        Assert.Equal(BackofficeMode.On, options.ModeFor(BackofficeModule.Docs));
    }

    [Fact]
    public void UnknownMode_KeepsTheModuleOff_AndIsOnlyRejectedWhenAsked()
    {
        var options = new P4BackofficeProductOptions { Mode = "Shadow", BaseUrl = "https://backoffice.example.com", SecretKey = "p4sk_test_abc" };

        Assert.Equal(BackofficeMode.Off, options.ModeFor(BackofficeModule.Docs));
        Assert.Equal(BackofficeMode.On, options.ModeFor(BackofficeModule.Email));
        Assert.Equal([("P4Backoffice:Mode (environment variable P4Backoffice__Mode)", "Shadow")], BackofficeOptionsValidator.InvalidModes(options));
        Assert.Contains(BackofficeOptionsValidator.Validate(options, rejectInvalidModes: true), p => p.Contains("P4Backoffice__Mode"));
        Assert.DoesNotContain(BackofficeOptionsValidator.Validate(options, rejectInvalidModes: false), p => p.Contains("P4Backoffice__Mode"));
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

    [Fact]
    public void ConsentDocumentWithoutAPage_NeedsDocsOn()
    {
        var off = new P4BackofficeProductOptions();
        off.Consents.Documents = ["terms", "custom_doc"];
        Assert.Contains(BackofficeOptionsValidator.Validate(off), p => p.Contains("custom_doc") && p.Contains("Docs"));

        var on = new P4BackofficeProductOptions { BaseUrl = "https://backoffice.example.com", SecretKey = "p4sk_test_abc", Environment = "test" };
        on.Modules.Docs = "On";
        on.Consents.Documents = ["terms", "custom_doc"];
        Assert.DoesNotContain(BackofficeOptionsValidator.Validate(on), p => p.Contains("custom_doc"));
    }
}
