using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;

namespace DotNetSigningServer.Tests.Services;

public class SealCredentialStoreTests
{
    [Fact]
    public void Pfx_is_read_once_and_the_parsed_key_is_reused()
    {
        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();
        var path = Path.Combine(Path.GetTempPath(), $"seal-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, Convert.FromBase64String(pfxBase64));
        try
        {
            var options = new SealOptions { Enabled = true, PfxPath = path, PfxPassword = password };
            var first = SealCredentialStore.Get(options);

            // The file is not touched again: gone from disk, same key object.
            File.Delete(path);
            var second = SealCredentialStore.Get(options);

            Assert.Same(first.PrivateKey, second.PrivateKey);
            Assert.NotEmpty(second.Chain);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Failed_load_is_not_cached()
    {
        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();
        var options = new SealOptions { Enabled = true, PfxBase64 = pfxBase64, PfxPassword = "wrong" };
        Assert.ThrowsAny<Exception>(() => SealCredentialStore.Get(options));

        options.PfxPassword = password;
        Assert.NotEmpty(SealCredentialStore.Get(options).Chain);
    }

    [Fact]
    public void Missing_certificate_is_reported()
    {
        Assert.Throws<InvalidOperationException>(() => SealCredentialStore.Get(new SealOptions { Enabled = true }));
    }

    [Fact]
    public void Sealing_service_signs_repeatedly_from_the_cached_key()
    {
        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();
        var options = new SealOptions { Enabled = true, PfxBase64 = pfxBase64, PfxPassword = password };
        var service = new PdfSealingService(TestHelpers.WrapOptions(options), null!, null!);

        Assert.True(service.DescribeCapability().Enabled);
        Assert.True(service.DescribeCapability().Enabled);
    }
}
