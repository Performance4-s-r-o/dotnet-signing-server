using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Services;
using iText.Commons.Digest;
using iText.Signatures;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// The portal names a backup authority so one TSA's outage does not refuse every
/// timestamped signature. These pin down when the backup is asked and what
/// surfaces when both fail.
/// </summary>
public class FallbackTsaClientTests
{
    private sealed class FakeTsa : ITSAClient
    {
        private readonly Func<byte[], byte[]> _answer;
        public int Calls { get; private set; }
        public int SizeEstimate { get; init; } = 4096;

        public FakeTsa(Func<byte[], byte[]> answer) => _answer = answer;

        public int GetTokenSizeEstimate() => SizeEstimate;
        public IMessageDigest GetMessageDigest() => throw new NotSupportedException();

        public byte[] GetTimeStampToken(byte[] imprint)
        {
            Calls++;
            return _answer(imprint);
        }
    }

    private static readonly byte[] Imprint = { 1, 2, 3 };

    [Fact]
    public void UsesThePrimaryAndLeavesTheBackupAlone()
    {
        var primary = new FakeTsa(_ => new byte[] { 0xA });
        var backup = new FakeTsa(_ => new byte[] { 0xB });
        var client = new FallbackTsaClient(primary, backup, "https://a", "https://b");

        Assert.Equal(new byte[] { 0xA }, client.GetTimeStampToken(Imprint));
        Assert.Equal(0, backup.Calls);
    }

    [Fact]
    public void AsksTheBackupWithTheSameImprintWhenThePrimaryFails()
    {
        byte[]? seen = null;
        var primary = new FakeTsa(_ => throw new HttpRequestException("503"));
        var backup = new FakeTsa(i => { seen = i; return new byte[] { 0xB }; });
        var client = new FallbackTsaClient(primary, backup, "https://a", "https://b");

        Assert.Equal(new byte[] { 0xB }, client.GetTimeStampToken(Imprint));
        Assert.Same(Imprint, seen);
    }

    [Fact]
    public void SurfacesTheBackupsFailureWhenBothFail()
    {
        var primary = new FakeTsa(_ => throw new HttpRequestException("primary down"));
        var backup = new FakeTsa(_ => throw new TimeoutException("backup down"));
        var client = new FallbackTsaClient(primary, backup, "https://a", "https://b");

        var ex = Assert.Throws<TimeoutException>(() => client.GetTimeStampToken(Imprint));
        Assert.Equal("backup down", ex.Message);
    }

    [Fact]
    public void ReservesRoomForTheLargerToken()
    {
        var client = new FallbackTsaClient(
            new FakeTsa(_ => Array.Empty<byte>()) { SizeEstimate = 4096 },
            new FakeTsa(_ => Array.Empty<byte>()) { SizeEstimate = 9000 },
            "https://a",
            "https://b");

        Assert.Equal(9000, client.GetTokenSizeEstimate());
    }

    [Fact]
    public void CreateTsaClient_WithoutBackup_IsAPlainClient()
    {
        var client = PdfCryptoHelper.CreateTsaClient("https://8.8.8.8/tsr", null, null);
        Assert.IsType<PinnedIpTsaClient>(client);
    }

    [Fact]
    public void CreateTsaClient_WithBackup_WrapsBoth()
    {
        var client = PdfCryptoHelper.CreateTsaClient(
            "https://8.8.8.8/tsr", null, null,
            new TsaEndpoint("https://1.1.1.1/tsr", "u", "p"));
        Assert.IsType<FallbackTsaClient>(client);
    }

    [Theory]
    [InlineData("http://tsa.example.com/tsr", "TSA_HTTPS_REQUIRED")]
    [InlineData("https://127.0.0.1/tsr", "TSA_HOST_NOT_ALLOWED")]
    public void CreateTsaClient_ChecksTheBackupLikeThePrimary(string backupUrl, string code)
    {
        // Both URLs arrive in the request; the backup gets no pass.
        var ex = Assert.Throws<ApiValidationException>(() =>
            PdfCryptoHelper.CreateTsaClient("https://8.8.8.8/tsr", null, null, new TsaEndpoint(backupUrl, null, null)));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void TsaEndpoint_From_NoUrl_MeansNoBackup()
    {
        Assert.Null(TsaEndpoint.From("  ", "u", "p"));
        Assert.Null(TsaEndpoint.From(null, null, null));
    }
}
