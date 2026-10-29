using System.Text;
using DotNetSigningServer.ExternalSignatures;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Bouncycastle.X509;
using iText.Commons.Bouncycastle.Cert;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Signatures;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using X509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace DotNetSigningServer.Tests.Spike;

/// <summary>
/// Generátor PDF pro spike validátoru (validator/SPIKE.md): stejné cesty jako produkce
/// (presign + sign, PFX, pečeť, LTV), vlastní testovací PKI, žádná síť.
/// Bez proměnné DSS_SPIKE_FIXTURES_DIR nic nedělá.
/// </summary>
public class DssValidatorFixtures
{
    // URL v certifikátu míří dovnitř: validátor je nesmí kontaktovat (SSRF).
    private const string CrlUrl = "http://127.0.0.1:9/spike/root.crl";
    private const string CaIssuersUrl = "http://169.254.169.254/latest/meta-data/ca.crt";
    private const string OcspUrl = "http://169.254.169.254/ocsp";

    private sealed record Key(X509Certificate Cert, AsymmetricKeyParameter Private);

    [Fact]
    public void Generate()
    {
        var dir = Environment.GetEnvironmentVariable("DSS_SPIKE_FIXTURES_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);

        var root = CreateRoot();
        var signer = CreateLeaf(root, "CN=Spike Signer, O=Spike Test PKI, C=CZ", timeStamping: false);
        var tsa = CreateLeaf(root, "CN=Spike TSA, O=Spike Test PKI, C=CZ", timeStamping: true);
        var rootCrl = CreateCrl(root);
        var tsaClient = new ChainTsaClient(tsa, root);

        File.WriteAllText(System.IO.Path.Combine(dir, "test-root.pem"), Pem(root.Cert));

        var signing = new PdfSigningService(
            TestHelpers.WrapOptions(new SealOptions()),
            TestHelpers.WrapOptions(new EvidenceOptions()),
            new PdfVisualSigningService());
        var chainPem = Pem(signer.Cert) + Pem(root.Cert);
        var unsigned = Convert.FromBase64String(TestHelpers.CreateMinimalPdfBase64());

        // Produkční cesta Fortify/SPFx: presign -> podpis atributů u klienta -> sign.
        var bb = ExternalSign(signing, unsigned, chainPem, signer, tsa: null, "Spike B-B", "Signature1");
        Write(dir, "01-ours-external-B-B.pdf", bb);

        var bt = ExternalSign(signing, unsigned, chainPem, signer, tsaClient, "Spike B-T", "Signature1");
        Write(dir, "02-ours-external-B-T.pdf", bt);

        var ltv = new PdfLtvService(new NoOcsp(), new FixedCrl(rootCrl));
        var blt = ltv.Extend(bt, tsaClient: null, addArchiveTimestamp: false);
        Write(dir, "03-ours-external-B-LT.pdf", blt);

        var blta = ltv.Extend(bt, tsaClient, addArchiveTimestamp: true);
        Write(dir, "04-ours-external-B-LTA.pdf", blta);

        // S ověřovací stránkou/QR jako na webu.
        var withQr = ExternalSign(signing, unsigned, chainPem, signer, tsaClient, "Spike QR", "Signature1",
            verificationUrl: "https://example.test/verify/spike");
        Write(dir, "05-ours-external-B-T-qr.pdf", withQr);

        // Druhý podpis přes první (dva podepisující, inkrementální revize).
        var second = ExternalSign(signing, bt, chainPem, signer, tsaClient, "Spike second", "Signature2");
        Write(dir, "06-ours-two-signatures.pdf", second);

        // Pečeť platformy (PFX z konfigurace), bez TSA.
        var (pfx, pfxPassword) = Pfx(signer, root);
        var sealOptions = new SealOptions { Enabled = true, PfxBase64 = pfx, PfxPassword = pfxPassword, Visible = false };
        var sealSigning = new PdfSigningService(
            TestHelpers.WrapOptions(sealOptions), TestHelpers.WrapOptions(new EvidenceOptions()), new PdfVisualSigningService());
        var sealing = new PdfSealingService(TestHelpers.WrapOptions(sealOptions), sealSigning, new PdfVisualSigningService());
        var sealedPdf = Convert.FromBase64String(sealing.ApplySeal(new SealInput
        {
            PdfContent = Convert.ToBase64String(unsigned),
            VerificationUrl = "https://example.test/verify/seal",
            VerificationMode = "qr",
        }));
        Write(dir, "07-ours-seal.pdf", sealedPdf);

        // /api/sign-pfx se samopodepsaným certifikátem (dnešní testovací helper).
        var (_, selfPfx, selfPassword) = TestHelpers.CreateTestCertificate();
        var selfSigned = Convert.FromBase64String(signing.SignWithPfx(new PfxSignInput
        {
            PdfContent = Convert.ToBase64String(unsigned), PfxContent = selfPfx, PfxPassword = selfPassword,
            FieldName = "Signature1", SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 },
        }));
        Write(dir, "08-ours-pfx-selfsigned-B-B.pdf", selfSigned);

        // Poškozené a změněné.
        Write(dir, "20-tampered-signed-byte.pdf", FlipInSignedRange(bb, "Spike B-B"));
        Write(dir, "21-appended-garbage.pdf", bb.Concat(Encoding.ASCII.GetBytes("\n% trailing bytes\n")).ToArray());
        Write(dir, "22-truncated.pdf", bb.Take(bb.Length - 64).ToArray());
        Write(dir, "23-incremental-page-content.pdf", AppendPageText(bt));
        Write(dir, "24-incremental-annotation.pdf", AppendAnnotation(bt));
    }

    private static byte[] ExternalSign(PdfSigningService signing, byte[] pdf, string chainPem, Key signer,
        ITSAClient? tsa, string reason, string field, string? verificationUrl = null)
    {
        var (path, hashHex) = signing.HandlePreSign(new PreSignInput
        {
            PdfContent = Convert.ToBase64String(pdf),
            CertificatePem = chainPem,
            Reason = reason,
            Location = "Spike",
            SignRect = new SignRect { X = 10, Y = field == "Signature1" ? 10 : 70, Width = 200, Height = 50 },
            SignPageNumber = 1,
            VerificationUrl = verificationUrl,
            VerificationMode = verificationUrl == null ? null : "qr",
        }, pdf, field);
        try
        {
            var attributes = Convert.FromHexString(hashHex);
            var s = SignerUtilities.GetSigner("SHA256WithRSA");
            s.Init(true, signer.Private);
            s.BlockUpdate(attributes, 0, attributes.Length);
            var signature = s.GenerateSignature();

            // Totéž co PdfSigningService.InjectFinalSignature, jen s TSA v procesu.
            var chain = PdfCryptoHelper.LoadCertificatesFromPemString(chainPem);
            using var input = new MemoryStream(File.ReadAllBytes(path));
            using var output = new MemoryStream();
            PdfSigner.SignDeferred(new PdfReader(input), field, output, new ExternalSignatureContainer(chain, signature, tsa));
            return output.ToArray();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] FlipInSignedRange(byte[] pdf, string marker)
    {
        var copy = (byte[])pdf.Clone();
        var needle = Encoding.ASCII.GetBytes(marker);
        for (var i = 0; i < copy.Length - needle.Length; i++)
        {
            if (copy.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                copy[i + 1] = (byte)'X';
                return copy;
            }
        }
        throw new InvalidOperationException("marker not found");
    }

    private static byte[] AppendPageText(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output), new StampingProperties().UseAppendMode()))
        {
            var canvas = new PdfCanvas(doc.GetFirstPage().NewContentStreamAfter(), doc.GetFirstPage().GetResources(), doc);
            canvas.BeginText().SetFontAndSize(PdfFontFactory.CreateFont(), 18).MoveText(100, 500)
                .ShowText("Amount: 1 000 000 EUR").EndText();
        }
        return output.ToArray();
    }

    private static byte[] AppendAnnotation(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output), new StampingProperties().UseAppendMode()))
        {
            var note = new PdfTextAnnotation(new Rectangle(300, 300, 20, 20));
            note.SetContents(new PdfString("Reviewer comment"));
            doc.GetFirstPage().AddAnnotation(note);
        }
        return output.ToArray();
    }

    private static Key CreateRoot()
    {
        var keys = Rsa();
        var name = new X509Name("CN=Spike Test Root CA, O=Spike Test PKI, C=CZ");
        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.ProbablePrime(120, new Random()));
        gen.SetIssuerDN(name);
        gen.SetSubjectDN(name);
        gen.SetNotBefore(DateTime.UtcNow.AddDays(-2));
        gen.SetNotAfter(DateTime.UtcNow.AddYears(5));
        gen.SetPublicKey(keys.Public);
        gen.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
        gen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign));
        return new Key(gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", keys.Private)), keys.Private);
    }

    private static Key CreateLeaf(Key issuer, string subject, bool timeStamping)
    {
        var keys = Rsa();
        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.ProbablePrime(120, new Random()));
        gen.SetIssuerDN(issuer.Cert.SubjectDN);
        gen.SetSubjectDN(new X509Name(subject));
        gen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        gen.SetNotAfter(DateTime.UtcNow.AddYears(2));
        gen.SetPublicKey(keys.Public);
        gen.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(false));
        if (timeStamping)
        {
            gen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature));
            gen.AddExtension(X509Extensions.ExtendedKeyUsage, true, new ExtendedKeyUsage(KeyPurposeID.IdKPTimeStamping));
        }
        else
        {
            gen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.NonRepudiation));
        }
        gen.AddExtension(X509Extensions.CrlDistributionPoints, false, new CrlDistPoint(new[]
        {
            new DistributionPoint(new DistributionPointName(new GeneralNames(
                new GeneralName(GeneralName.UniformResourceIdentifier, CrlUrl))), null, null),
        }));
        gen.AddExtension(X509Extensions.AuthorityInfoAccess, false, new AuthorityInformationAccess(new[]
        {
            new AccessDescription(AccessDescription.IdADCAIssuers, new GeneralName(GeneralName.UniformResourceIdentifier, CaIssuersUrl)),
            new AccessDescription(AccessDescription.IdADOcsp, new GeneralName(GeneralName.UniformResourceIdentifier, OcspUrl)),
        }));
        return new Key(gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", issuer.Private)), keys.Private);
    }

    private static byte[] CreateCrl(Key issuer)
    {
        var gen = new X509V2CrlGenerator();
        gen.SetIssuerDN(issuer.Cert.SubjectDN);
        gen.SetThisUpdate(DateTime.UtcNow.AddMinutes(-5));
        gen.SetNextUpdate(DateTime.UtcNow.AddDays(30));
        gen.AddExtension(X509Extensions.CrlNumber, false, new CrlNumber(BigInteger.One));
        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", issuer.Private)).GetEncoded();
    }

    private static AsymmetricCipherKeyPair Rsa()
    {
        var g = new RsaKeyPairGenerator();
        g.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        return g.GenerateKeyPair();
    }

    private static (string Base64, string Password) Pfx(Key leaf, Key root)
    {
        const string password = "spike";
        var store = new Pkcs12StoreBuilder().Build();
        store.SetKeyEntry("seal", new AsymmetricKeyEntry(leaf.Private),
            new[] { new X509CertificateEntry(leaf.Cert), new X509CertificateEntry(root.Cert) });
        using var ms = new MemoryStream();
        store.Save(ms, password.ToCharArray(), new SecureRandom());
        return (Convert.ToBase64String(ms.ToArray()), password);
    }

    private static string Pem(X509Certificate cert)
    {
        using var sw = new StringWriter();
        var w = new PemWriter(sw);
        w.WriteObject(cert);
        w.Writer.Flush();
        return sw.ToString();
    }

    private static void Write(string dir, string name, byte[] bytes) =>
        File.WriteAllBytes(System.IO.Path.Combine(dir, name), bytes);

    /// <summary>RFC 3161 v procesu s certifikátem TSA vydaným testovací kořenovou CA.</summary>
    private sealed class ChainTsaClient : TSAClientBouncyCastle
    {
        private readonly Key _tsa;
        private readonly Key _root;
        private int _serial;

        internal ChainTsaClient(Key tsa, Key root) : base("http://tsa.invalid/")
        {
            _tsa = tsa;
            _root = root;
        }

        protected override byte[] GetTSAResponse(byte[] requestBytes)
        {
            var request = new TimeStampRequest(requestBytes);
            var generator = new TimeStampTokenGenerator(_tsa.Private, _tsa.Cert, TspAlgorithms.Sha256, "1.3.6.1.4.1.13762.3");
            generator.SetCertificates(CollectionUtilities.CreateStore(new[] { _tsa.Cert, _root.Cert }));
            var response = new TimeStampResponseGenerator(generator, TspAlgorithms.Allowed)
                .Generate(request, BigInteger.ValueOf(Interlocked.Increment(ref _serial)), DateTime.UtcNow);
            return response.GetEncoded();
        }
    }

    private sealed class NoOcsp : IOcspClient
    {
        public byte[]? GetEncoded(IX509Certificate checkCert, IX509Certificate issuerCert, string url) => null;
    }

    private sealed class FixedCrl : ICrlClient
    {
        private readonly byte[] _crl;
        internal FixedCrl(byte[] crl) => _crl = crl;
        public ICollection<byte[]> GetEncoded(IX509Certificate checkCert, string url) => new[] { _crl };
    }
}
