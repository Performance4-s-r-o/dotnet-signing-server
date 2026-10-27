using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Bouncycastle.X509;
using iText.Bouncycastle.Crypto;
using iText.Commons.Bouncycastle.Cert;
using iText.Commons.Digest;
using iText.Kernel.Pdf;
using iText.Kernel.Crypto;
using iText.Signatures;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.Utilities.Collections;
using X509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// The inspector must not claim more than the file proves (#81 b): a timestamp
/// that does not verify is no timestamp, validation data for one signature does
/// not raise another, a ByteRange with holes or overlaps is reported as such,
/// and every revision gets its own fingerprint. All keys are generated here;
/// nothing touches the network.
/// </summary>
public class PdfSignatureInspectorHonestyTests
{
    private static readonly Lazy<(X509Certificate Certificate, AsymmetricKeyParameter Key)> Signer =
        new(() => CreateSigner("CN=Inspector Signer, O=Unit Tests"));

    private static readonly Lazy<(X509Certificate Certificate, AsymmetricKeyParameter Key)> SecondSigner =
        new(() => CreateSigner("CN=Second Signer, O=Unit Tests"));

    private static readonly Lazy<(X509Certificate Certificate, AsymmetricKeyParameter Key)> Tsa =
        new(TestHelpers.CreateTsaCertificate);

    private enum TokenMode
    {
        Honest,
        /// <summary>A real token, but over a hash of something else.</summary>
        ForeignImprint,
        /// <summary>The right imprint, but the token's signature value is damaged.</summary>
        CorruptSignature,
    }

    /// <summary>
    /// Builds tokens in-process without iText's response check, so a test can
    /// embed a token that a real client would have refused.
    /// </summary>
    private sealed class CraftedTsaClient : ITSAClient
    {
        private readonly TokenMode _mode;
        private readonly ITSAClient _digestSource = new TSAClientBouncyCastle("http://tsa.invalid/");
        private static int _serial;

        internal CraftedTsaClient(TokenMode mode) => _mode = mode;

        public int GetTokenSizeEstimate() => 8192;

        public IMessageDigest GetMessageDigest() => _digestSource.GetMessageDigest();

        public byte[] GetTimeStampToken(byte[] imprint)
        {
            var stamped = _mode == TokenMode.ForeignImprint
                ? SHA256.HashData(Encoding.ASCII.GetBytes("a different document"))
                : imprint;

            var requestGenerator = new TimeStampRequestGenerator();
            requestGenerator.SetCertReq(true);
            var request = requestGenerator.Generate(TspAlgorithms.Sha256, stamped);

            var (certificate, key) = Tsa.Value;
            var generator = new TimeStampTokenGenerator(key, certificate, TspAlgorithms.Sha256, "1.3.6.1.4.1.13762.3");
            generator.SetCertificates(CollectionUtilities.CreateStore(new[] { certificate }));
            var token = generator.Generate(request, BigInteger.ValueOf(Interlocked.Increment(ref _serial)), DateTime.UtcNow);

            var bytes = token.GetEncoded();
            // The SignerInfo signature is the last element of the token, so the
            // last byte is part of it and the token still parses.
            if (_mode == TokenMode.CorruptSignature) bytes[^1] ^= 0xFF;
            return bytes;
        }
    }

    private static (X509Certificate, AsymmetricKeyParameter) CreateSigner(string subject)
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var keys = generator.GenerateKeyPair();

        var certificateGenerator = new Org.BouncyCastle.X509.X509V3CertificateGenerator();
        var name = new X509Name(subject);
        certificateGenerator.SetSerialNumber(BigInteger.ProbablePrime(120, new Random()));
        certificateGenerator.SetIssuerDN(name);
        certificateGenerator.SetSubjectDN(name);
        certificateGenerator.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certificateGenerator.SetNotAfter(DateTime.UtcNow.AddYears(1));
        certificateGenerator.SetPublicKey(keys.Public);
        return (certificateGenerator.Generate(new Asn1SignatureFactory("SHA256WithRSA", keys.Private)), keys.Private);
    }

    private static byte[] UnsignedPdf() => Convert.FromBase64String(TestHelpers.CreateMinimalPdfBase64());

    private static byte[] Sign(
        byte[] pdf,
        string fieldName,
        ITSAClient? tsa,
        (X509Certificate Certificate, AsymmetricKeyParameter Key)? signer = null)
    {
        var (certificate, key) = signer ?? Signer.Value;
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var pdfSigner = new PdfSigner(new PdfReader(input), output, new StampingProperties().UseAppendMode());
        pdfSigner.SetSignerProperties(new SignerProperties().SetFieldName(fieldName));
        pdfSigner.SignDetached(
            new PrivateKeySignature(new PrivateKeyBC(key), DigestAlgorithms.SHA256),
            new IX509Certificate[] { new X509CertificateBC(certificate) },
            null, null, tsa, 0, PdfSigner.CryptoStandard.CADES);
        return output.ToArray();
    }

    private static byte[] DocumentTimestamp(byte[] pdf, string fieldName, TokenMode mode = TokenMode.Honest)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var pdfSigner = new PdfSigner(new PdfReader(input), output, new StampingProperties().UseAppendMode());
        pdfSigner.Timestamp(new CraftedTsaClient(mode), fieldName);
        return output.ToArray();
    }

    /// <summary>Adds a DSS with a VRI entry for one named signature only.</summary>
    private static byte[] AddValidationDataFor(byte[] pdf, string fieldName, X509Certificate certificate)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var document = new PdfDocument(new PdfReader(input), new PdfWriter(output), new StampingProperties().UseAppendMode());
        var verification = new LtvVerification(document);
        verification.AddVerification(fieldName, new List<byte[]>(), new List<byte[]>(), new List<byte[]> { certificate.GetEncoded() });
        verification.Merge();
        document.Close();
        return output.ToArray();
    }

    private static SignatureInspectionEntry Field(SignatureInspectionResult result, string name) =>
        result.Signatures.Single(s => s.FieldName == name);

    // ---- contract ----------------------------------------------------------

    /// <summary>Existing callers read `level`; it stays in the response next to `structuralLevel`.</summary>
    [Fact]
    public void Response_KeepsTheDeprecatedLevelNextToStructuralLevel()
    {
        var result = PdfSignatureInspector.Inspect(Sign(UnsignedPdf(), "Signature1", tsa: null));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var signature = json.RootElement.GetProperty("signatures")[0];
        Assert.Equal("B-B", signature.GetProperty("structuralLevel").GetString());
        Assert.Equal("B-B", signature.GetProperty("level").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("revisions").GetArrayLength());
        Assert.Equal(64, json.RootElement.GetProperty("documentSha256").GetString()!.Length);
    }

    // ---- timestamps ------------------------------------------------------

    [Fact]
    public void VerifiedSignatureTimestamp_IsBT()
    {
        var result = PdfSignatureInspector.Inspect(Sign(UnsignedPdf(), "Signature1", new CraftedTsaClient(TokenMode.Honest)));

        var signature = Assert.Single(result.Signatures);
        Assert.True(signature.HasTimestamp);
        Assert.True(signature.TimestampSignatureValid);
        Assert.True(signature.TimestampMessageImprintMatches);
        Assert.True(signature.TimestampVerified);
        Assert.Equal("B-T", signature.StructuralLevel);
        Assert.True(signature.IntegrityVerified);
    }

    /// <summary>
    /// A token taken from another document still carries a real signature and
    /// a real time, but it does not stamp this signature. Reporting B-T would
    /// let anyone paste a timestamp onto a signature it never saw.
    /// </summary>
    [Fact]
    public void TimestampWithForeignMessageImprint_IsNotBT()
    {
        var result = PdfSignatureInspector.Inspect(Sign(UnsignedPdf(), "Signature1", new CraftedTsaClient(TokenMode.ForeignImprint)));

        var signature = Assert.Single(result.Signatures);
        Assert.True(signature.HasTimestamp, "the token is there and is reported as present");
        Assert.True(signature.TimestampSignatureValid, "the token itself is genuinely signed");
        Assert.False(signature.TimestampMessageImprintMatches);
        Assert.False(signature.TimestampVerified);
        Assert.Equal("B-B", signature.StructuralLevel);
        // The PDF signature is untouched by what its token claims.
        Assert.True(signature.IntegrityVerified);
    }

    [Fact]
    public void TimestampWithDamagedSignature_IsNotBT()
    {
        var result = PdfSignatureInspector.Inspect(Sign(UnsignedPdf(), "Signature1", new CraftedTsaClient(TokenMode.CorruptSignature)));

        var signature = Assert.Single(result.Signatures);
        Assert.True(signature.HasTimestamp);
        Assert.False(signature.TimestampSignatureValid);
        Assert.True(signature.TimestampMessageImprintMatches);
        Assert.False(signature.TimestampVerified);
        Assert.Equal("B-B", signature.StructuralLevel);
    }

    /// <summary>A verified document timestamp in a later revision gives the signature its time (PAdES B-T).</summary>
    [Fact]
    public void VerifiedDocumentTimestampOverABbSignature_IsBT()
    {
        var pdf = DocumentTimestamp(Sign(UnsignedPdf(), "Signature1", tsa: null), "DocTs1");

        var result = PdfSignatureInspector.Inspect(pdf);

        var documentTimestamp = Field(result, "DocTs1");
        Assert.Equal("archive-timestamp", documentTimestamp.StructuralLevel);
        Assert.True(documentTimestamp.TimestampVerified);
        Assert.Equal("B-T", Field(result, "Signature1").StructuralLevel);
    }

    [Fact]
    public void DocumentTimestampWithForeignImprint_DoesNotRaiseTheSignature()
    {
        var pdf = DocumentTimestamp(Sign(UnsignedPdf(), "Signature1", tsa: null), "DocTs1", TokenMode.ForeignImprint);

        var result = PdfSignatureInspector.Inspect(pdf);

        var documentTimestamp = Field(result, "DocTs1");
        Assert.True(documentTimestamp.HasTimestamp);
        Assert.False(documentTimestamp.TimestampMessageImprintMatches);
        Assert.False(documentTimestamp.TimestampVerified);
        Assert.Equal("B-B", Field(result, "Signature1").StructuralLevel);
    }

    // ---- validation data per signature ----------------------------------

    /// <summary>
    /// The DSS is one dictionary for the whole file, but it covers only the
    /// signatures it was written for. Validation data added for the second
    /// signature says nothing about the first.
    /// </summary>
    [Fact]
    public void ValidationDataOnlyForTheLaterSignature_RaisesOnlyThatSignature()
    {
        var tsa = new CraftedTsaClient(TokenMode.Honest);
        var pdf = Sign(UnsignedPdf(), "Signature1", tsa);
        pdf = Sign(pdf, "Signature2", tsa, SecondSigner.Value);
        pdf = AddValidationDataFor(pdf, "Signature2", SecondSigner.Value.Certificate);

        var result = PdfSignatureInspector.Inspect(pdf);

        Assert.True(result.HasDss);
        Assert.Equal("B-T", Field(result, "Signature1").StructuralLevel);
        Assert.Null(Field(result, "Signature1").ValidationDataRevision);

        var second = Field(result, "Signature2");
        Assert.Equal("B-LT", second.StructuralLevel);
        Assert.Equal(result.Revisions.Count, second.ValidationDataRevision);
        Assert.True(second.ValidationDataRevision > second.FileRevision);
    }

    /// <summary>
    /// Validation data for the first signature, then a second signature: the
    /// second one has none yet, however much the first one has.
    /// </summary>
    [Fact]
    public void SignatureAddedAfterValidationData_StaysBT()
    {
        var tsa = new CraftedTsaClient(TokenMode.Honest);
        var pdf = Sign(UnsignedPdf(), "Signature1", tsa);
        pdf = AddValidationDataFor(pdf, "Signature1", Signer.Value.Certificate);
        pdf = Sign(pdf, "Signature2", tsa);

        var result = PdfSignatureInspector.Inspect(pdf);

        Assert.Equal("B-LT", Field(result, "Signature1").StructuralLevel);
        Assert.Equal("B-T", Field(result, "Signature2").StructuralLevel);
    }

    [Fact]
    public void DocumentTimestampOverValidationData_IsBLtaOnlyForTheCoveredSignature()
    {
        var tsa = new CraftedTsaClient(TokenMode.Honest);
        var pdf = Sign(UnsignedPdf(), "Signature1", tsa);
        pdf = Sign(pdf, "Signature2", tsa, SecondSigner.Value);
        pdf = AddValidationDataFor(pdf, "Signature2", SecondSigner.Value.Certificate);
        pdf = DocumentTimestamp(pdf, "DocTs1");

        var result = PdfSignatureInspector.Inspect(pdf);

        Assert.Equal("B-T", Field(result, "Signature1").StructuralLevel);
        Assert.Equal("B-LTA", Field(result, "Signature2").StructuralLevel);
    }

    /// <summary>Validation data without any verified time is not B-LT.</summary>
    [Fact]
    public void ValidationDataWithoutTimestamp_StaysBB()
    {
        var pdf = Sign(UnsignedPdf(), "Signature1", tsa: null);
        pdf = AddValidationDataFor(pdf, "Signature1", Signer.Value.Certificate);

        var result = PdfSignatureInspector.Inspect(pdf);

        Assert.True(result.HasDss);
        Assert.Equal("B-B", Field(result, "Signature1").StructuralLevel);
    }

    // ---- revisions -------------------------------------------------------

    [Fact]
    public void MultipleRevisions_EachHasItsOwnFingerprint()
    {
        var tsa = new CraftedTsaClient(TokenMode.Honest);
        var pdf = Sign(UnsignedPdf(), "Signature1", tsa);
        pdf = Sign(pdf, "Signature2", tsa);
        pdf = DocumentTimestamp(pdf, "DocTs1");

        var result = PdfSignatureInspector.Inspect(pdf);

        // The unsigned original, two signatures, the document timestamp.
        Assert.Equal(4, result.Revisions.Count);
        Assert.Equal(Enumerable.Range(1, 4), result.Revisions.Select(r => r.Revision));
        Assert.True(result.Revisions.Zip(result.Revisions.Skip(1)).All(p => p.First.EndOffset < p.Second.EndOffset));
        Assert.Equal(pdf.LongLength, result.Revisions[^1].EndOffset);
        foreach (var revision in result.Revisions)
        {
            var expected = Convert.ToHexString(SHA256.HashData(pdf.AsSpan(0, (int)revision.EndOffset))).ToLowerInvariant();
            Assert.Equal(expected, revision.Sha256);
        }

        Assert.Equal(Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant(), result.DocumentSha256);
        Assert.Equal(pdf.LongLength, result.ByteLength);
        Assert.Equal(result.Revisions[^1].Sha256, result.DocumentSha256);

        // Each signature ends exactly where its revision ends.
        Assert.Equal(new int?[] { 2, 3, 4 }, new[] { "Signature1", "Signature2", "DocTs1" }.Select(n => Field(result, n).FileRevision));
        foreach (var signature in result.Signatures)
        {
            Assert.True(signature.ByteRangeSane);
            var range = signature.ByteRange!;
            Assert.Equal(result.Revisions[signature.FileRevision!.Value - 1].EndOffset, range[2] + range[3]);
        }
    }

    /// <summary>
    /// The first revision of an unsigned file is the whole file: a %%EOF inside
    /// a stream (an attached PDF, say) must not split it.
    /// </summary>
    [Fact]
    public void EofMarkerInsideAStream_IsNotARevision()
    {
        var pdf = Encoding.ASCII.GetBytes(
            "%PDF-1.7\n1 0 obj\n<< /Length 40 >>\nstream\nstartxref\n999999\n%%EOF\nendstream\nendobj\n" +
            "xref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size 1 >>\nstartxref\n");
        var xrefOffset = Encoding.ASCII.GetString(pdf).IndexOf("xref\n0 1", StringComparison.Ordinal);
        pdf = pdf.Concat(Encoding.ASCII.GetBytes($"{xrefOffset}\n%%EOF\n")).ToArray();

        var ends = PdfRevisionScanner.FindRevisionEnds(pdf, Array.Empty<long>());

        Assert.Equal(new[] { pdf.LongLength }, ends);
    }

    // ---- ByteRange -------------------------------------------------------

    [Fact]
    public void ByteRangeOfAnOrdinarySignature_IsSane()
    {
        var signature = Assert.Single(PdfSignatureInspector.Inspect(Sign(UnsignedPdf(), "Signature1", tsa: null)).Signatures);

        Assert.True(signature.ByteRangeSane);
        Assert.Equal(4, signature.ByteRange!.Length);
        Assert.Equal(0, signature.ByteRange[0]);
    }

    public static IEnumerable<object[]> BrokenRanges() => new[]
    {
        // An earlier revision's signature legitimately ends before the file does,
        // so a short range is not on this list.
        new object[] { "ends past the file", (Func<long[], long, long[]>)((r, n) => new[] { r[0], r[1], r[2], r[3] + 1000 }) },
        new object[] { "hole after /Contents", (Func<long[], long, long[]>)((r, n) => new[] { r[0], r[1], r[2] + 4, r[3] - 4 }) },
        new object[] { "overlapping ranges", (Func<long[], long, long[]>)((r, n) => new[] { r[0], r[2] + 10, r[2], r[3] }) },
        new object[] { "first range not at 0", (Func<long[], long, long[]>)((r, n) => new[] { 10, r[1] - 10, r[2], r[3] }) },
        new object[] { "six values", (Func<long[], long, long[]>)((r, n) => new[] { r[0], r[1], r[2], r[3] - 10, n - 10, 10 }) },
    };

    /// <summary>
    /// A signature whose ByteRange leaves bytes outside /Contents unsigned, or
    /// points at bytes that are not there, must be reported as such, and the
    /// inspector must still answer rather than fail.
    /// </summary>
    [Theory]
    [MemberData(nameof(BrokenRanges))]
    public void NonsensicalByteRange_IsReportedNotSane(string name, Func<long[], long, long[]> transform)
    {
        var pdf = Sign(UnsignedPdf(), "Signature1", new CraftedTsaClient(TokenMode.Honest));
        var original = Assert.Single(PdfSignatureInspector.Inspect(pdf).Signatures).ByteRange!;

        var tampered = RewriteByteRange(pdf, transform(original, pdf.LongLength));
        var signature = Assert.Single(PdfSignatureInspector.Inspect(tampered).Signatures);

        Assert.False(signature.ByteRangeSane, name);
        Assert.Null(signature.FileRevision);
        Assert.Null(signature.ValidationDataRevision);
    }

    /// <summary>
    /// Rewrites the (only) /ByteRange array in place, padded with spaces so no
    /// offset in the file moves.
    /// </summary>
    private static byte[] RewriteByteRange(byte[] pdf, long[] range)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var key = text.LastIndexOf("/ByteRange", StringComparison.Ordinal);
        var open = text.IndexOf('[', key);
        var close = text.IndexOf(']', open);
        var end = close + 1;
        while (end < text.Length && text[end] == ' ') end++;

        var replacement = "[" + string.Join(' ', range) + "]";
        var slot = end - open;
        Assert.True(replacement.Length <= slot, "the reserved ByteRange slot is too short for the test value");

        var result = (byte[])pdf.Clone();
        var bytes = Encoding.ASCII.GetBytes(replacement.PadRight(slot));
        Buffer.BlockCopy(bytes, 0, result, open, bytes.Length);
        return result;
    }
}
