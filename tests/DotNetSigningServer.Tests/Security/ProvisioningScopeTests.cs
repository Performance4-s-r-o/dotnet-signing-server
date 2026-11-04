using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Provisioning;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace DotNetSigningServer.Tests.Security;

/// <summary>#131 without a database: the main key's scope, inputs, inheritance and limits.</summary>
public class ProvisioningScopeTests
{
    [Theory]
    [InlineData("POST", "/api/provisioning/accounts", true)]
    [InlineData("GET", "/api/provisioning/accounts", true)]
    [InlineData("GET", "/api/provisioning/accounts/", true)]
    [InlineData("GET", "/api/capabilities", true)]
    [InlineData("DELETE", "/api/provisioning/accounts", false)]
    [InlineData("POST", "/api/presign", false)]
    [InlineData("POST", "/api/sign", false)]
    [InlineData("GET", "/api/operations/x", false)]
    [InlineData("POST", "/api/account/tokens/rotate", false)]
    [InlineData("DELETE", "/api/account", false)]
    [InlineData("OPTIONS", "/api/presign", false)]
    public void The_provisioning_token_reaches_provisioning_and_capabilities_only(string method, string path, bool allowed)
    {
        Assert.Equal(allowed, ProvisioningTokenScope.IsAllowed(method, new PathString(path)));
    }

    [Theory]
    [InlineData("/api/provisioning", true)]
    [InlineData("/api/provisioning/accounts", true)]
    [InlineData("/api/PROVISIONING/accounts", true)]
    [InlineData("/api/provisioningx", false)]
    [InlineData("/api/presign", false)]
    public void Provisioning_paths_are_recognised(string path, bool expected)
    {
        Assert.Equal(expected, ProvisioningTokenScope.IsProvisioningPath(new PathString(path)));
    }

    [Theory]
    [InlineData("platform", true)]
    [InlineData("3f2b7c1e-8d4a-4b6f-9c2e-1a5d7e9f0b3c", true)]
    [InlineData("a.b_c:d-e", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    public void External_ids_are_a_safe_alphabet(string? value, bool expected)
    {
        Assert.Equal(expected, AccountProvisioner.IsValidExternalId(value));
        Assert.False(AccountProvisioner.IsValidExternalId(new string('a', User.MaxExternalIdLength + 1)));
    }

    [Fact]
    public void A_provisioned_account_inherits_and_refuses_to_answer_without_its_parent()
    {
        var parent = new User { IsEnterprise = true, SealAllowed = true, IsActive = true };
        var child = new User { ParentUserId = parent.Id, IsActive = true };
        Assert.Throws<InvalidOperationException>(() => child.EffectiveEnterprise);
        Assert.Throws<InvalidOperationException>(() => child.EffectiveSealAllowed);
        Assert.Throws<InvalidOperationException>(() => child.EffectiveActive);

        child.Parent = parent;
        Assert.True(child.EffectiveEnterprise);
        Assert.True(child.EffectiveSealAllowed);
        Assert.True(child.EffectiveActive);
        parent.IsActive = false;
        Assert.False(child.EffectiveActive);

        var ordinary = new User { IsEnterprise = false, IsActive = true };
        Assert.False(ordinary.EffectiveEnterprise);
        Assert.True(ordinary.EffectiveActive);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    [Fact]
    public void Provisioning_limits_have_defaults_and_refuse_nonsense()
    {
        var defaults = ProvisioningOptions.From(Config());
        Assert.Equal(50, defaults.MaxAccountsPerDay);
        Assert.Equal(10, defaults.AlertAccountsPerHour);
        Assert.Equal(0, ProvisioningOptions.From(Config((ProvisioningOptions.MaxAccountsPerDayVariable, "0"))).MaxAccountsPerDay);
        Assert.Equal(50, ProvisioningOptions.From(Config((ProvisioningOptions.MaxAccountsPerDayVariable, "{{environment.DOTNET_PROVISIONING_MAX_ACCOUNTS_PER_DAY}}"))).MaxAccountsPerDay);
        Assert.Throws<InvalidOperationException>(() => ProvisioningOptions.From(Config((ProvisioningOptions.MaxAccountsPerDayVariable, "-1"))));
        Assert.Throws<InvalidOperationException>(() => ProvisioningOptions.From(Config((ProvisioningOptions.AlertAccountsPerHourVariable, "many"))));
    }

    [Theory]
    [InlineData(false, null, 8085, true)]
    [InlineData(true, null, 8085, true)]
    [InlineData(true, 8086, 8086, true)]
    [InlineData(true, 8086, 8085, false)]
    [InlineData(false, 8086, 8085, true)]
    public void Only_the_provisioning_token_is_bound_to_the_internal_listener(bool provisioning, int? internalPort, int localPort, bool expected)
    {
        Assert.Equal(expected, ApiKeyAuthenticationHandler.AcceptedOn(new InternalListenerOptions { InternalPort = internalPort }, localPort, provisioning));
    }
}
