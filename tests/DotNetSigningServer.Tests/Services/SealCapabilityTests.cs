using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// A caller asks this before putting someone through a signing wizard. Answering
/// "yes" when the certificate cannot actually be loaded is worse than answering
/// "no": the failure then lands on the last step, after the work is done.
/// </summary>
public class SealCapabilityTests
{
    private static PdfSealingService Build(SealOptions options) =>
        new(TestHelpers.WrapOptions(options), null!, null!);

    [Fact]
    public void Disabled_ReportsUnavailableWithoutTouchingAnyFile()
    {
        var capability = Build(new SealOptions { Enabled = false, PfxPath = "/does/not/exist.pfx" })
            .DescribeCapability();

        Assert.False(capability.Enabled);
        Assert.Null(capability.Error);
    }

    [Fact]
    public void EnabledWithNoCertificate_ReportsUnavailableRatherThanThrowing()
    {
        // The state every fresh self-hosted deployment starts in.
        var capability = Build(new SealOptions { Enabled = true }).DescribeCapability();

        Assert.False(capability.Enabled);
        Assert.Equal("SEAL_CERTIFICATE_UNREADABLE", capability.Error);
    }

    [Fact]
    public void EnabledWithUnreadableCertificate_SaysSoInsteadOfClaimingItWorks()
    {
        // Wrong password is the likeliest version of this, and it must not read
        // as "sealing is available".
        var capability = Build(new SealOptions
        {
            Enabled = true,
            PfxBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
            PfxPassword = "wrong",
        }).DescribeCapability();

        Assert.False(capability.Enabled);
        Assert.Equal("SEAL_CERTIFICATE_UNREADABLE", capability.Error);
    }

    [Fact]
    public void EnabledWithRealCertificate_ReportsWhoSealsAndUntilWhen()
    {
        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();

        var capability = Build(new SealOptions
        {
            Enabled = true,
            PfxBase64 = pfxBase64,
            PfxPassword = password,
        }).DescribeCapability();

        Assert.True(capability.Enabled);
        // Whoever the certificate says — not a name from configuration.
        Assert.Contains("Test Signer", capability.Subject);
        // The expiry is the part nobody watches on a self-hosted install.
        Assert.NotNull(capability.NotAfter);
        Assert.True(capability.NotAfter > DateTimeOffset.UtcNow);
    }
}
