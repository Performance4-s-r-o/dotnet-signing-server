using DotNetSigningServer.Options;

namespace DotNetSigningServer.Tests.Options;

public class DataProtectionKeyPolicyTests
{
    [Theory]
    [InlineData(true, false, null, true)]
    [InlineData(true, false, "", true)]
    [InlineData(true, false, "false", true)]
    [InlineData(true, false, "TRUE", false)]
    public void Production_RefusesPlaintextUnlessAllowedOnPurpose(bool production, bool migrate, string? allow, bool refuse)
    {
        var outcome = DataProtectionKeyPolicy.Evaluate(production, migrate, allow);
        Assert.NotNull(outcome);
        Assert.Equal(refuse, outcome!.Refuse);
    }

    [Fact]
    public void OutsideProduction_OrMigrateStep_SaysNothing()
    {
        Assert.Null(DataProtectionKeyPolicy.Evaluate(false, false, null));
        Assert.Null(DataProtectionKeyPolicy.Evaluate(true, true, null));
    }
}
