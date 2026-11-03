using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Bouncycastle.Crypto;
using iText.Bouncycastle.X509;
using iText.Commons.Bouncycastle.Cert;
using iText.Kernel.Pdf;
using iText.Signatures;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Ocsp;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// B-LT of one named signature (the caller's own, just added) must not pass on
/// the strength of revocation data that only an earlier signature got. The
/// fixture is a small PKI: one root, two signers and a timestamping authority,
/// all issued by the root, so each certificate needs its own revocation data.
/// </summary>
public class PdfLtvRequiredSignatureTests
{
    private sealed class Pki
    {
        internal readonly (X509Certificate Cert, AsymmetricKeyParameter Key) Root;
        internal readonly (X509Certificate Cert, AsymmetricKeyParameter Key) Earlier;
        internal readonly (X509Certificate Cert, AsymmetricKeyParameter Key) Seal;
        internal readonly (X509Certificate Cert, AsymmetricKeyParameter Key) Tsa;

        internal Pki()
        {
            Root = Issue("CN=Test Root", null, ca: true);
            Earlier = Issue("CN=Earlier Signer", Root);
            Seal = Issue("CN=Platform Seal", Root);
            Tsa = Issue("CN=Test TSA", Root, timeStamping: true);
        }

        /// <summary>An empty CRL from the root: valid revocation evidence for every certificate it issued.</summary>
        internal byte[] RootCrl()
        {
            var generator = new X509V2CrlGenerator();
            generator.SetIssuerDN(Root.Cert.SubjectDN);
            generator.SetThisUpdate(DateTime.UtcNow.AddMinutes(-5));
            generator.SetNextUpdate(DateTime.UtcNow.AddYears(1));
            return generator.Generate(new Asn1SignatureFactory("SHA256WithRSA", Root.Key)).GetEncoded();
        }

        private static (X509Certificate, AsymmetricKeyParameter) Issue(
            string dn,
            (X509Certificate Cert, AsymmetricKeyParameter Key)? issuer,
            bool ca = false,
            bool timeStamping = false)
        {
            var keyPairGen = new RsaKeyPairGenerator();
            keyPairGen.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
            var keyPair = keyPairGen.GenerateKeyPair();

            var subject = new X509Name(dn + ", O=Unit Tests");
            var certGen = new X509V3CertificateGenerator();
            certGen.SetSerialNumber(BigInteger.ProbablePrime(120, new Random()));
            certGen.SetIssuerDN(issuer?.Cert.SubjectDN ?? subject);
            certGen.SetSubjectDN(subject);
            certGen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
            certGen.SetNotAfter(DateTime.UtcNow.AddYears(1));
            certGen.SetPublicKey(keyPair.Public);
            if (ca)
            {
                certGen.AddExtension(X509Extensions.BasicConstraints, critical: true, new BasicConstraints(true));
            }
            if (timeStamping)
            {
                certGen.AddExtension(
                    X509Extensions.ExtendedKeyUsage,
                    critical: true,
                    new ExtendedKeyUsage(KeyPurposeID.IdKPTimeStamping));
            }

            var signingKey = issuer?.Key ?? keyPair.Private;
            return (certGen.Generate(new Asn1SignatureFactory("SHA256WithRSA", signingKey)), keyPair.Private);
        }
    }

    /// <summary>Hands out the root's CRL only for the certificates listed, and remembers who was asked.</summary>
    private sealed class SelectiveCrlClient : ICrlClient
    {
        private readonly byte[] _crl;
        private readonly HashSet<string> _covered;
        internal readonly List<string> Asked = new();

        internal SelectiveCrlClient(byte[] crl, params X509Certificate[] covered)
        {
            _crl = crl;
            _covered = covered.Select(c => c.SubjectDN.ToString()).ToHashSet();
        }

        public ICollection<byte[]> GetEncoded(IX509Certificate checkCert, string url)
        {
            var subject = checkCert.GetSubjectDN().ToString();
            Asked.Add(subject);
            return _covered.Contains(subject) ? new List<byte[]> { _crl } : new List<byte[]>();
        }
    }

    /// <summary>Real OCSP answers signed by the root, with the given status for every certificate.</summary>
    private sealed class RootOcsp : IOcspClient
    {
        private readonly Pki _pki;
        private readonly CertificateStatus? _status;
        internal RootOcsp(Pki pki, CertificateStatus? status) => (_pki, _status) = (pki, status);

        public byte[]? GetEncoded(IX509Certificate checkCert, IX509Certificate issuerCert, string url)
        {
            var check = ((X509CertificateBC)checkCert).GetCertificate();
            var generator = new BasicOcspRespGenerator(new RespID(_pki.Root.Cert.SubjectDN));
            generator.AddResponse(new CertificateID(CertificateID.DigestSha1, _pki.Root.Cert, check.SerialNumber), _status);
            return generator
                .Generate(new Asn1SignatureFactory("SHA256WithRSA", _pki.Root.Key), new[] { _pki.Root.Cert }, DateTime.UtcNow)
                .GetEncoded();
        }
    }

    private sealed class NoOcsp : IOcspClient
    {
        public byte[]? GetEncoded(IX509Certificate checkCert, IX509Certificate issuerCert, string url) => null;
    }

    /// <summary>The earlier signature, then the seal with a signature timestamp from the PKI's own TSA.</summary>
    private static byte[] EarlierThenSealed(Pki pki)
    {
        var pdf = Convert.FromBase64String(TestHelpers.CreateMinimalPdfBase64());
        var withEarlier = Sign(pdf, "Earlier1", pki.Earlier, pki.Root.Cert, tsa: null);
        return Sign(withEarlier, "PlatformSeal1", pki.Seal, pki.Root.Cert, new LocalTsaClient(pki.Tsa.Cert, pki.Tsa.Key, pki.Root.Cert));
    }

    private static byte[] Sign(
        byte[] pdf,
        string fieldName,
        (X509Certificate Cert, AsymmetricKeyParameter Key) signer,
        X509Certificate root,
        ITSAClient? tsa)
    {
        using var output = new MemoryStream();
        var pdfSigner = new PdfSigner(new PdfReader(new MemoryStream(pdf)), output, new StampingProperties().UseAppendMode());
        pdfSigner.SetSignerProperties(new SignerProperties().SetFieldName(fieldName));
        IX509Certificate[] chain = { new X509CertificateBC(signer.Cert), new X509CertificateBC(root) };
        pdfSigner.SignDetached(
            new BouncyCastleDigest(),
            new PrivateKeySignature(new PrivateKeyBC(signer.Key), "SHA256"),
            chain,
            null,
            null,
            tsa,
            0,
            PdfSigner.CryptoStandard.CMS);
        return output.ToArray();
    }

    /// <summary>
    /// The reported gap, kept as a regression: without a required signature the
    /// call stays best effort, so data for the earlier signature alone is enough
    /// to "succeed" — and the timestamp authority is never asked at all.
    /// </summary>
    [Fact]
    public void Extend_WithoutRequiredSignature_PassesOnAnEarlierSignaturesDataAlone()
    {
        var pki = new Pki();
        var crl = new SelectiveCrlClient(pki.RootCrl(), pki.Earlier.Cert);
        var service = new PdfLtvService(new NoOcsp(), crl);

        var extended = service.Extend(EarlierThenSealed(pki), tsaClient: null, addArchiveTimestamp: false);

        Assert.True(PdfSignatureInspector.Inspect(extended).HasDss);
        Assert.DoesNotContain(crl.Asked, s => s.Contains("CN=Test TSA"));
    }

    [Fact]
    public void Extend_RequiredSignatureWithoutItsOwnData_IsRefused()
    {
        var pki = new Pki();
        var service = new PdfLtvService(new NoOcsp(), new SelectiveCrlClient(pki.RootCrl(), pki.Earlier.Cert));

        var ex = Assert.Throws<ApiValidationException>(
            () => service.Extend(EarlierThenSealed(pki), null, addArchiveTimestamp: false, requireCompleteFor: "PlatformSeal1"));

        Assert.Equal("INCOMPLETE_REVOCATION_DATA", ex.Code);
        Assert.Contains("CN=Platform Seal", ex.Message);
    }

    [Fact]
    public void Extend_RequiredSignatureWithoutTimestampAuthorityData_IsRefused()
    {
        var pki = new Pki();
        var service = new PdfLtvService(new NoOcsp(), new SelectiveCrlClient(pki.RootCrl(), pki.Earlier.Cert, pki.Seal.Cert));

        var ex = Assert.Throws<ApiValidationException>(
            () => service.Extend(EarlierThenSealed(pki), null, addArchiveTimestamp: false, requireCompleteFor: "PlatformSeal1"));

        Assert.Equal("INCOMPLETE_REVOCATION_DATA", ex.Code);
        Assert.Contains("CN=Test TSA", ex.Message);
        Assert.DoesNotContain("CN=Platform Seal", ex.Message);
    }

    [Fact]
    public void Extend_RequiredSignatureWithCompleteData_EmbedsItAndKeepsSignaturesIntact()
    {
        var pki = new Pki();
        var crl = new SelectiveCrlClient(pki.RootCrl(), pki.Seal.Cert, pki.Tsa.Cert);
        var service = new PdfLtvService(new NoOcsp(), crl);

        var extended = service.Extend(EarlierThenSealed(pki), null, addArchiveTimestamp: false, requireCompleteFor: "PlatformSeal1");

        var result = PdfSignatureInspector.Inspect(extended);
        Assert.True(result.HasDss);
        Assert.Equal(2, result.Signatures.Count);
        Assert.All(result.Signatures, s => Assert.True(s.IntegrityVerified, $"{s.FieldName} no longer verifies"));
        // The earlier signature stays best effort: it got nothing and that does not fail the call.
        Assert.Contains(crl.Asked, s => s.Contains("CN=Earlier Signer"));
    }

    [Fact]
    public void Extend_RequiredSignatureNotInDocument_IsRefused()
    {
        var pki = new Pki();
        var service = new PdfLtvService(new NoOcsp(), new SelectiveCrlClient(pki.RootCrl(), pki.Earlier.Cert, pki.Seal.Cert, pki.Tsa.Cert));

        var ex = Assert.Throws<ApiValidationException>(
            () => service.Extend(EarlierThenSealed(pki), null, addArchiveTimestamp: false, requireCompleteFor: "Missing1"));

        Assert.Equal("SIGNATURE_NOT_FOUND", ex.Code);
    }

    [Fact]
    public void Extend_RequiredSignatureWithGoodOcsp_Passes()
    {
        var pki = new Pki();
        var service = new PdfLtvService(new RootOcsp(pki, CertificateStatus.Good), new SelectiveCrlClient(pki.RootCrl()));

        var extended = service.Extend(EarlierThenSealed(pki), null, addArchiveTimestamp: false, requireCompleteFor: "PlatformSeal1");

        Assert.True(PdfSignatureInspector.Inspect(extended).HasDss);
    }

    /// <summary>A "revoked" answer is not evidence of validity, and without a CRL there is nothing else.</summary>
    [Fact]
    public void Extend_RequiredSignatureWithRevokedOcspOnly_IsRefused()
    {
        var pki = new Pki();
        var revoked = new RevokedStatus(DateTime.UtcNow.AddHours(-1), CrlReason.KeyCompromise);
        var service = new PdfLtvService(new RootOcsp(pki, revoked), new SelectiveCrlClient(pki.RootCrl()));

        var ex = Assert.Throws<ApiValidationException>(
            () => service.Extend(EarlierThenSealed(pki), null, addArchiveTimestamp: false, requireCompleteFor: "PlatformSeal1"));

        Assert.Equal("INCOMPLETE_REVOCATION_DATA", ex.Code);
    }
}
