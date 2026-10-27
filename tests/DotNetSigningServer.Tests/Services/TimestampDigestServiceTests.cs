using System.Security.Cryptography;
using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Commons.Digest;
using iText.Signatures;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Tsp;

namespace DotNetSigningServer.Tests.Services;

public class TimestampDigestServiceTests
{
    private static readonly byte[] Digest = SHA256.HashData("merkle root"u8.ToArray());

    [Fact]
    public void Hex_and_base64_give_the_same_32_bytes()
    {
        var hex = Convert.ToHexString(Digest);
        var b64 = Convert.ToBase64String(Digest);

        Assert.Equal(Digest, TimestampDigestService.ParseDigest(hex));
        Assert.Equal(Digest, TimestampDigestService.ParseDigest(hex.ToLowerInvariant()));
        Assert.Equal(Digest, TimestampDigestService.ParseDigest(b64));
        Assert.Equal(Digest, TimestampDigestService.ParseDigest(b64.TrimEnd('=')));
        Assert.Equal(Digest, TimestampDigestService.ParseDigest("  " + hex + "\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abcd")]
    [InlineData("not base64 and not hex, not even close to a digest!!")]
    public void Wrong_shape_is_refused(string? value)
    {
        Assert.Equal("DIGEST_INVALID",
            Assert.Throws<ApiValidationException>(() => TimestampDigestService.ParseDigest(value)).Code);
    }

    [Fact]
    public void Other_lengths_are_refused()
    {
        foreach (var length in new[] { 20, 31, 33, 64 })
        {
            var bytes = new byte[length];
            Assert.Throws<ApiValidationException>(() => TimestampDigestService.ParseDigest(Convert.ToHexString(bytes)));
            Assert.Throws<ApiValidationException>(() => TimestampDigestService.ParseDigest(Convert.ToBase64String(bytes)));
        }
    }

    [Fact]
    public void Token_covers_the_digest_and_reports_time_and_authority()
    {
        var before = DateTime.UtcNow.AddMinutes(-1);

        var result = TimestampDigestService.Stamp(new LocalTsaClient(), Digest, "https://tsa.example/tsr");

        var token = new TimeStampToken(new CmsSignedData(Convert.FromBase64String(result.Token)));
        Assert.Equal(Digest, token.TimeStampInfo.GetMessageImprintDigest());
        Assert.Equal("SHA-256", result.DigestAlgorithm);
        Assert.True(result.GenTime >= before && result.GenTime <= DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(DateTimeKind.Utc, result.GenTime.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.TsaSubject));
    }

    private sealed class FakeTsa : ITSAClient
    {
        private readonly Func<byte[], byte[]> _answer;
        public FakeTsa(Func<byte[], byte[]> answer) => _answer = answer;
        public int GetTokenSizeEstimate() => 4096;
        public IMessageDigest GetMessageDigest() => throw new NotSupportedException();
        public byte[] GetTimeStampToken(byte[] imprint) => _answer(imprint);
    }

    [Fact]
    public void The_authority_is_asked_with_the_circuit_breaker_bypassed()
    {
        var local = new LocalTsaClient();
        bool? bypassed = null;
        TimestampDigestService.Stamp(new FakeTsa(i =>
        {
            bypassed = TsaCircuitBreaker.IsBypassed;
            return local.GetTimeStampToken(i);
        }), Digest, "https://tsa.example/tsr");

        Assert.True(bypassed);
        Assert.False(TsaCircuitBreaker.IsBypassed);
    }

    [Fact]
    public void Token_for_another_digest_is_not_passed_on()
    {
        var local = new LocalTsaClient();
        var other = SHA256.HashData("something else"u8.ToArray());
        var foreign = local.GetTimeStampToken(other);

        var ex = Assert.Throws<TsaCommunicationException>(() =>
            TimestampDigestService.Stamp(new FakeTsa(_ => foreign), Digest, "https://tsa.example/tsr"));
        Assert.Equal("https://tsa.example/tsr", ex.TsaUrl);
    }

    [Fact]
    public void Empty_garbage_and_transport_failures_become_tsa_communication_errors()
    {
        foreach (Func<byte[], byte[]> answer in new Func<byte[], byte[]>[]
        {
            _ => Array.Empty<byte>(),
            _ => new byte[] { 1, 2, 3 },
            _ => throw new HttpRequestException("503"),
        })
        {
            Assert.Throws<TsaCommunicationException>(() =>
                TimestampDigestService.Stamp(new FakeTsa(answer), Digest, "https://tsa.example/tsr"));
        }
    }

    [Fact]
    public void Authority_must_be_named()
    {
        Assert.Equal("TSA_URL_REQUIRED",
            Assert.Throws<ApiValidationException>(() =>
                TimestampDigestService.CreateClient(new TimestampDigestInput { Digest = "x" })).Code);
    }

    [Theory]
    [InlineData("http://tsa.example.com/tsr", "TSA_HTTPS_REQUIRED")]
    [InlineData("https://127.0.0.1/tsr", "TSA_HOST_NOT_ALLOWED")]
    [InlineData("https://169.254.169.254/latest/meta-data/", "TSA_HOST_NOT_ALLOWED")]
    public void Authority_faces_the_same_address_checks_as_every_timestamp(string url, string code)
    {
        Assert.Equal(code,
            Assert.Throws<ApiValidationException>(() =>
                TimestampDigestService.CreateClient(new TimestampDigestInput { TsaUrl = url })).Code);
    }

    [Theory]
    [InlineData("https://8.8.8.8:8443/tsr", "TSA_PORT_NOT_ALLOWED")]
    [InlineData("https://8.8.8.8:444/tsr", "TSA_PORT_NOT_ALLOWED")]
    [InlineData("https://user:secret@8.8.8.8/tsr", "TSA_URL_INVALID")]
    [InlineData("https://user@8.8.8.8/tsr", "TSA_URL_INVALID")]
    public void Port_and_credentials_in_the_url_are_refused(string url, string code)
    {
        Assert.Equal(code,
            Assert.Throws<ApiValidationException>(() =>
                TimestampDigestService.CreateClient(new TimestampDigestInput { TsaUrl = url })).Code);
        Assert.Equal(code,
            Assert.Throws<ApiValidationException>(() =>
                TimestampDigestService.CreateClient(new TimestampDigestInput { TsaUrl = "https://8.8.8.8/tsr", TsaBackupUrl = url })).Code);
    }

    [Fact]
    public void Explicit_default_port_is_fine()
    {
        Assert.NotNull(TimestampDigestService.CreateClient(new TimestampDigestInput { TsaUrl = "https://8.8.8.8:443/tsr" }));
    }

    [Fact]
    public void The_gate_is_bounded()
    {
        var taken = 0;
        try
        {
            while (TimestampDigestService.Gate.Wait(0)) taken++;
            Assert.Equal(TimestampDigestService.MaxConcurrent, taken);
        }
        finally
        {
            TimestampDigestService.Gate.Release(taken);
        }
    }

    [Fact]
    public void A_backup_authority_is_checked_too()
    {
        var input = new TimestampDigestInput { TsaUrl = "https://8.8.8.8/tsr", TsaBackupUrl = "https://10.0.0.5/tsr" };
        Assert.Equal("TSA_HOST_NOT_ALLOWED",
            Assert.Throws<ApiValidationException>(() => TimestampDigestService.CreateClient(input)).Code);
    }
}
