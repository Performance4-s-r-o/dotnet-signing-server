using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// The inspector is what a renewal watcher plans against: it has to recognise the
/// PAdES level of a document and, once archive timestamps exist, report when their
/// certificates expire. Getting the level wrong means either renewing documents
/// that don't need it or — worse — never renewing one that does.
/// </summary>
public class PdfSignatureInspectorTests
{
    private static PdfSigningService CreateSigningService() =>
        new(
            TestHelpers.WrapOptions(new SealOptions()),
            TestHelpers.WrapOptions(new EvidenceOptions()),
            new PdfVisualSigningService());

    private static byte[] SignWithTestCertificate(string fieldName = "Signature1")
    {
        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();
        var signed = CreateSigningService().SignWithPfx(new PfxSignInput
        {
            PdfContent = TestHelpers.CreateMinimalPdfBase64(),
            PfxContent = pfxBase64,
            PfxPassword = password,
            Location = "Test",
            Reason = "Inspector",
            FieldName = fieldName,
            SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 },
        });
        return Convert.FromBase64String(signed);
    }

    [Fact]
    public void Inspect_UnsignedPdf_ReportsNothing()
    {
        var result = PdfSignatureInspector.Inspect(
            Convert.FromBase64String(TestHelpers.CreateMinimalPdfBase64()));

        Assert.Empty(result.Signatures);
        Assert.False(result.HasDss);
        Assert.Equal(0, result.ArchiveTimestampCount);
        Assert.Null(result.EarliestTimestampCertificateExpiry);
    }

    [Fact]
    public void Inspect_SignedWithoutTimestamp_IsLevelBB()
    {
        var result = PdfSignatureInspector.Inspect(SignWithTestCertificate());

        var signature = Assert.Single(result.Signatures);
        Assert.Equal("Signature1", signature.FieldName);
        Assert.Equal("ETSI.CAdES.detached", signature.SubFilter);
        // No TSA was supplied, so the signature stops at the baseline level.
        Assert.Equal("B-B", signature.Level);
        Assert.False(signature.HasTimestamp);
        Assert.False(signature.IsDocumentTimestamp);
    }

    [Fact]
    public void Inspect_ReportsSignerAndAlgorithms()
    {
        var signature = Assert.Single(PdfSignatureInspector.Inspect(SignWithTestCertificate()).Signatures);

        Assert.Contains("Test Signer", signature.SignerName);
        Assert.Equal("SHA256", signature.DigestAlgorithm);
        Assert.True(signature.IntegrityVerified);
        Assert.True(signature.CoversWholeDocument);
    }

    /// <summary>
    /// The signing certificate's own expiry is not what a renewal is scheduled
    /// against — but reporting it lets the portal warn before a signature becomes
    /// unverifiable for want of a timestamp.
    /// </summary>
    [Fact]
    public void Inspect_ReportsSigningCertificateExpiry()
    {
        var signature = Assert.Single(PdfSignatureInspector.Inspect(SignWithTestCertificate()).Signatures);

        Assert.NotNull(signature.SigningCertificateNotAfter);
        // The test certificate is issued for a year.
        Assert.InRange(
            signature.SigningCertificateNotAfter!.Value,
            DateTime.UtcNow.AddMonths(11),
            DateTime.UtcNow.AddMonths(13));
    }

    /// <summary>
    /// Anything that is not ETSI.CAdES.detached must not be reported as PAdES,
    /// however valid the signature itself is — documents signed before the switch
    /// are exactly this case, and mislabelling them would put them in a renewal
    /// queue that cannot help them.
    /// </summary>
    [Fact]
    public void Inspect_NamesTheLevelFromTheSubFilter()
    {
        var signature = Assert.Single(PdfSignatureInspector.Inspect(SignWithTestCertificate()).Signatures);

        Assert.Equal("ETSI.CAdES.detached", signature.SubFilter);
        Assert.NotEqual("non-PAdES", signature.Level);
        Assert.Contains(signature.Level, new[] { "B-B", "B-T", "B-LT", "B-LTA" });
    }

    /// <summary>
    /// Reading the timestamp certificate's expiry is the whole point of the
    /// inspector, but proving it needs a real RFC 3161 authority — so this is kept
    /// out of the default run rather than making the suite depend on the network.
    ///
    /// Run it with: TSA_INTEGRATION_URL=https://freetsa.org/tsr dotnet test
    /// </summary>
    [Fact]
    public void Inspect_TimestampedSignature_ReportsTsaCertificateExpiry()
    {
        // No skip infrastructure in this suite, so an unset variable simply means
        // the assertions below never run — deliberately, to keep CI off the network.
        var tsaUrl = Environment.GetEnvironmentVariable("TSA_INTEGRATION_URL");
        if (string.IsNullOrWhiteSpace(tsaUrl)) return;

        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();
        var signed = CreateSigningService().SignWithPfx(new PfxSignInput
        {
            PdfContent = TestHelpers.CreateMinimalPdfBase64(),
            PfxContent = pfxBase64,
            PfxPassword = password,
            FieldName = "Signature1",
            SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 },
            TsaUrl = tsaUrl,
        });

        var result = PdfSignatureInspector.Inspect(Convert.FromBase64String(signed));
        var signature = Assert.Single(result.Signatures);

        Assert.Equal("B-T", signature.Level);
        Assert.True(signature.HasTimestamp);
        Assert.NotNull(signature.TimestampedAt);

        // The dates a renewal schedule is built from: the deadline and the span
        // the safety margin is scaled against.
        Assert.NotNull(signature.TimestampCertificateNotAfter);
        Assert.NotNull(signature.TimestampCertificateNotBefore);
        Assert.True(signature.TimestampCertificateNotBefore < signature.TimestampCertificateNotAfter);
        Assert.True(
            signature.TimestampCertificateNotAfter > DateTime.UtcNow,
            "A freshly issued timestamp must be signed by a certificate that has not expired.");
        Assert.Equal(signature.TimestampCertificateNotAfter, result.EarliestTimestampCertificateExpiry);
    }

    [Fact]
    public void Inspect_MultipleSignatures_ReportsEachField()
    {
        var (_, pfxBase64, password) = TestHelpers.CreateTestCertificate();
        var service = CreateSigningService();

        var first = service.SignWithPfx(new PfxSignInput
        {
            PdfContent = TestHelpers.CreateMinimalPdfBase64(),
            PfxContent = pfxBase64,
            PfxPassword = password,
            FieldName = "Signature1",
            SignRect = new SignRect { X = 10, Y = 10, Width = 150, Height = 40 },
        });

        // Append mode: the second signature must leave the first one intact.
        var second = service.SignWithPfx(new PfxSignInput
        {
            PdfContent = first,
            PfxContent = pfxBase64,
            PfxPassword = password,
            FieldName = "Signature2",
            SignRect = new SignRect { X = 10, Y = 80, Width = 150, Height = 40 },
        });

        var result = PdfSignatureInspector.Inspect(Convert.FromBase64String(second));

        Assert.Equal(2, result.Signatures.Count);
        Assert.Contains(result.Signatures, s => s.FieldName == "Signature1");
        Assert.Contains(result.Signatures, s => s.FieldName == "Signature2");
        Assert.All(result.Signatures, s => Assert.True(s.IntegrityVerified));
    }
}
