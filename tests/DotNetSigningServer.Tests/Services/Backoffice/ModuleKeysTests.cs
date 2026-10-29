using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;

namespace DotNetSigningServer.Tests.Services.Backoffice;

public class ModuleKeysTests
{
    private static P4BackofficeProductOptions Options(string? environment = "test") => new()
    {
        Mode = "On",
        BaseUrl = "https://backoffice.example.com",
        SecretKey = "p4sk_test_main",
        Environment = environment,
    };

    [Fact]
    public void AModuleWithoutAKey_UsesTheMainOne_AndAnOwnKeyWinsForThatModuleOnly()
    {
        var o = Options();
        o.Keys.Billing = " p4sk_test_billing ";

        Assert.Equal("p4sk_test_billing", o.KeyFor(BackofficeModule.Billing));
        Assert.Equal("p4sk_test_main", o.KeyFor(BackofficeModule.Pricing));
        Assert.Equal("p4sk_test_main", o.KeyFor(BackofficeModule.Email)); // outbox modules: main key
    }

    [Fact]
    public void AnOwnKey_FollowsTheSamePrefixAndEnvironmentRules()
    {
        var o = Options("live");
        o.SecretKey = "p4sk_live_main";
        o.Keys.Docs = "p4sk_test_docs";
        o.Keys.Support = "nope";

        var problems = BackofficeOptionsValidator.Validate(o);

        Assert.Contains(problems, p => p.Contains("Keys:Docs") && p.Contains("test key"));
        Assert.Contains(problems, p => p.Contains("Keys:Support") && p.Contains("must start with"));
        Assert.DoesNotContain(problems, p => p.Contains("p4sk_test_docs"));
    }

    [Fact]
    public void ValidOwnKeys_AreAccepted()
    {
        var o = Options();
        o.Keys.Billing = "p4sk_test_b";
        Assert.Empty(BackofficeOptionsValidator.Validate(o));
    }
}
