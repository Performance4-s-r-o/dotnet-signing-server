using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Kernel.Pdf;
using iText.Signatures;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// #81: /api/sign embeds the client's signature only when it verifies with the certificate's
/// key over the presigned signed attributes. Keys are generated here, nothing is stored on disk.
/// </summary>
public class ClientSignatureVerificationTests : IDisposable
{
    private readonly PdfSigningService _sut = new(
        TestHelpers.WrapOptions(new SealOptions()),
        TestHelpers.WrapOptions(new EvidenceOptions()),
        new PdfVisualSigningService());

    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { }
        }
    }

    private sealed record Signer(string CertificatePem, AsymmetricKeyParameter PrivateKey);

    private static Signer NewRsaSigner(int bits = 2048) => NewSigner(
        GenerateRsa(bits), "SHA256WithRSA");

    private static Signer NewEcSigner() => NewEcSigner(SecObjectIdentifiers.SecP256r1);

    private static Signer NewEcSigner(DerObjectIdentifier curve)
    {
        var generator = new ECKeyPairGenerator("EC");
        generator.Init(new ECKeyGenerationParameters(curve, new SecureRandom()));
        return NewSigner(generator.GenerateKeyPair(), "SHA256WithECDSA");
    }

    private static AsymmetricCipherKeyPair GenerateRsa(int bits = 2048)
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new KeyGenerationParameters(new SecureRandom(), bits));
        return generator.GenerateKeyPair();
    }

    /// <summary>ECDSA as WebCrypto returns it: IEEE P1363 <c>r‖s</c>, each padded to the curve size.</summary>
    private static byte[] SignP1363(AsymmetricKeyParameter key, byte[] data)
    {
        var ecKey = (ECPrivateKeyParameters)key;
        var size = (ecKey.Parameters.N.BitLength + 7) / 8;
        var digest = new Sha256Digest();
        digest.BlockUpdate(data, 0, data.Length);
        var hash = new byte[digest.GetDigestSize()];
        digest.DoFinal(hash, 0);
        var ecdsa = new ECDsaSigner();
        ecdsa.Init(true, new ParametersWithRandom(key, new SecureRandom()));
        var rs = ecdsa.GenerateSignature(hash);
        var raw = new byte[size * 2];
        var r = rs[0].ToByteArrayUnsigned();
        var s = rs[1].ToByteArrayUnsigned();
        r.CopyTo(raw.AsSpan(size - r.Length));
        s.CopyTo(raw.AsSpan(2 * size - s.Length));
        return raw;
    }

    /// <summary>The portal's conversion: minimal DER integers, short or long (0x81) length form.</summary>
    private static byte[] P1363ToDer(byte[] raw)
    {
        static byte[] Integer(ReadOnlySpan<byte> value)
        {
            var start = 0;
            while (start < value.Length - 1 && value[start] == 0) start++;
            var body = value[start..].ToArray();
            if ((body[0] & 0x80) != 0) body = new byte[] { 0 }.Concat(body).ToArray();
            return new byte[] { 0x02, (byte)body.Length }.Concat(body).ToArray();
        }

        var half = raw.Length / 2;
        var content = Integer(raw.AsSpan(0, half)).Concat(Integer(raw.AsSpan(half))).ToArray();
        var header = content.Length < 0x80
            ? new byte[] { 0x30, (byte)content.Length }
            : new byte[] { 0x30, 0x81, (byte)content.Length };
        return header.Concat(content).ToArray();
    }

    private static Signer NewSigner(AsymmetricCipherKeyPair keyPair, string certificateAlgorithm)
    {
        var dn = new X509Name("CN=Client Signer, O=Unit Tests");
        var certGen = new X509V3CertificateGenerator();
        certGen.SetSerialNumber(BigInteger.ProbablePrime(120, new Random()));
        certGen.SetIssuerDN(dn);
        certGen.SetSubjectDN(dn);
        certGen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certGen.SetNotAfter(DateTime.UtcNow.AddYears(1));
        certGen.SetPublicKey(keyPair.Public);
        var certificate = certGen.Generate(new Asn1SignatureFactory(certificateAlgorithm, keyPair.Private));

        using var sw = new StringWriter();
        var pemWriter = new PemWriter(sw);
        pemWriter.WriteObject(certificate);
        pemWriter.Writer.Flush();
        return new Signer(sw.ToString(), keyPair.Private);
    }

    private (string Path, byte[] SignedAttributes) Presign(Signer signer)
    {
        var (path, hashHex) = _sut.HandlePreSign(new PreSignInput
        {
            PdfContent = TestHelpers.CreateMinimalPdfBase64(),
            CertificatePem = signer.CertificatePem,
            Location = "Test",
            Reason = "Client signature check",
            SignRect = new SignRect { X = 10, Y = 10, Width = 200, Height = 50 },
            SignPageNumber = 1,
        }, "Signature1");
        _tempFiles.Add(path);
        return (path, Convert.FromHexString(hashHex));
    }

    private static byte[] Sign(string algorithm, AsymmetricKeyParameter key, byte[] data)
    {
        var signer = SignerUtilities.GetSigner(algorithm);
        signer.Init(true, key);
        signer.BlockUpdate(data, 0, data.Length);
        return signer.GenerateSignature();
    }

    private string Finish(string path, Signer signer, byte[] signature) =>
        Finish(path, signer, Convert.ToHexString(signature).ToLowerInvariant());

    private string Finish(string path, Signer signer, string signedHash) =>
        _sut.HandleSign(new SignInput { Id = "test", SignedHash = signedHash }, path, signer.CertificatePem, "Signature1");

    private static void AssertVerifies(string signedPdfBase64)
    {
        using var ms = new MemoryStream(Convert.FromBase64String(signedPdfBase64));
        using var doc = new PdfDocument(new PdfReader(ms));
        var util = new SignatureUtil(doc);
        Assert.True(util.ReadSignatureData("Signature1").VerifySignatureIntegrityAndAuthenticity());
    }

    private SignatureInvalidException AssertRefused(string path, Signer signer, string signedHash, string reason)
    {
        var ex = Assert.Throws<SignatureInvalidException>(() => Finish(path, signer, signedHash));
        Assert.Equal("SIGNATURE_INVALID", ex.Code);
        Assert.Equal(reason, ex.Reason);
        return ex;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Fact]
    public void Valid_rsa_signature_is_embedded_and_verifies()
    {
        var signer = NewRsaSigner();
        var (path, attributes) = Presign(signer);

        AssertVerifies(Finish(path, signer, Sign("SHA256withRSA", signer.PrivateKey, attributes)));
    }

    [Fact]
    public void Valid_ecdsa_der_signature_is_embedded_and_verifies()
    {
        var signer = NewEcSigner();
        var (path, attributes) = Presign(signer);

        // BouncyCastle's ECDSA signer outputs DER, like the portal sends after converting r‖s.
        AssertVerifies(Finish(path, signer, Sign("SHA256withECDSA", signer.PrivateKey, attributes)));
    }

    public static IEnumerable<object[]> WebCryptoCurves() => new[]
    {
        new object[] { "P-256", SecObjectIdentifiers.SecP256r1, false },
        new object[] { "P-384", SecObjectIdentifiers.SecP384r1, false },
        new object[] { "P-521", SecObjectIdentifiers.SecP521r1, true },
    };

    [Theory]
    [MemberData(nameof(WebCryptoCurves))]
    public void Webcrypto_ecdsa_converted_to_der_is_embedded_and_verifies(string curveName, DerObjectIdentifier curve, bool longForm)
    {
        var signer = NewEcSigner(curve);
        var (path, attributes) = Presign(signer);

        // P-521 almost always needs the long length form; retry the rare short one so both forms are covered.
        byte[] der;
        var attempts = 0;
        do
        {
            der = P1363ToDer(SignP1363(signer.PrivateKey, attributes));
        } while ((der[1] == 0x81) != longForm && ++attempts < 20);
        Assert.True((der[1] == 0x81) == longForm, $"{curveName}: expected {(longForm ? "long" : "short")} DER length form");

        AssertVerifies(Finish(path, signer, der));
    }

    [Theory]
    [InlineData(3072)]
    [InlineData(4096)]
    public void Webcrypto_rsa_pkcs1_of_larger_keys_is_embedded_and_verifies(int bits)
    {
        var signer = NewRsaSigner(bits);
        var (path, attributes) = Presign(signer);

        // RSASSA-PKCS1-v1_5 with SHA-256 is exactly what WebCrypto produces.
        AssertVerifies(Finish(path, signer, Sign("SHA256withRSA", signer.PrivateKey, attributes)));
    }

    [Fact]
    public void Ecdsa_signed_pdf_names_ecdsa_in_the_signer_info()
    {
        var signer = NewEcSigner();
        var (path, attributes) = Presign(signer);
        var signed = Finish(path, signer, Sign("SHA256withECDSA", signer.PrivateKey, attributes));

        using var ms = new MemoryStream(Convert.FromBase64String(signed));
        using var doc = new PdfDocument(new PdfReader(ms));
        var contents = new SignatureUtil(doc).GetSignatureDictionary("Signature1").GetAsString(PdfName.Contents).GetValueBytes();
        using var asn1 = new Asn1InputStream(contents);
        var cms = new Org.BouncyCastle.Cms.CmsSignedData(
            new Org.BouncyCastle.Cms.CmsProcessableByteArray(Array.Empty<byte>()), asn1.ReadObject().GetEncoded());
        var info = cms.GetSignerInfos().GetSigners().Cast<Org.BouncyCastle.Cms.SignerInformation>().Single();
        // ECDSA OIDs live under 1.2.840.10045; rsaEncryption here would make the PDF unverifiable.
        Assert.StartsWith("1.2.840.10045.", info.EncryptionAlgOid);
    }

    [Fact]
    public void Signature_over_other_data_is_refused()
    {
        var signer = NewRsaSigner();
        var (path, attributes) = Presign(signer);
        attributes[^1] ^= 0x01;

        AssertRefused(path, signer, Hex(Sign("SHA256withRSA", signer.PrivateKey, attributes)), SignatureInvalidReason.Mismatch);
    }

    [Fact]
    public void Signature_with_another_key_is_refused()
    {
        var signer = NewRsaSigner();
        var other = GenerateRsa();
        var (path, attributes) = Presign(signer);

        AssertRefused(path, signer, Hex(Sign("SHA256withRSA", other.Private, attributes)), SignatureInvalidReason.Mismatch);
    }

    [Fact]
    public void Ecdsa_signature_with_another_key_is_refused()
    {
        var signer = NewEcSigner();
        var other = NewEcSigner();
        var (path, attributes) = Presign(signer);

        AssertRefused(path, signer, Hex(Sign("SHA256withECDSA", other.PrivateKey, attributes)), SignatureInvalidReason.Mismatch);
    }

    [Fact]
    public void Truncated_rsa_signature_is_refused()
    {
        var signer = NewRsaSigner();
        var (path, attributes) = Presign(signer);
        var signature = Sign("SHA256withRSA", signer.PrivateKey, attributes);

        AssertRefused(path, signer, Hex(signature[..^1]), SignatureInvalidReason.Malformed);
    }

    [Fact]
    public void Truncated_ecdsa_signature_is_refused()
    {
        var signer = NewEcSigner();
        var (path, attributes) = Presign(signer);
        var signature = Sign("SHA256withECDSA", signer.PrivateKey, attributes);

        AssertRefused(path, signer, Hex(signature[..^1]), SignatureInvalidReason.Malformed);
    }

    [Fact]
    public void Raw_ecdsa_r_s_without_der_is_refused()
    {
        var signer = NewEcSigner();
        var (path, attributes) = Presign(signer);
        var der = Sign("SHA256withECDSA", signer.PrivateKey, attributes);
        var sequence = Asn1Sequence.GetInstance(der);
        var r = DerInteger.GetInstance(sequence[0]).Value.ToByteArrayUnsigned();
        var s = DerInteger.GetInstance(sequence[1]).Value.ToByteArrayUnsigned();
        var raw = new byte[64];
        r.CopyTo(raw.AsSpan(32 - r.Length));
        s.CopyTo(raw.AsSpan(64 - s.Length));

        AssertRefused(path, signer, Hex(raw), SignatureInvalidReason.Malformed);
    }

    [Fact]
    public void Rsa_pss_signature_is_refused_as_unsupported()
    {
        var signer = NewRsaSigner();
        var (path, attributes) = Presign(signer);
        var pss = new PssSigner(new RsaBlindedEngine(), new Sha256Digest(), 32);
        pss.Init(true, new ParametersWithRandom(signer.PrivateKey, new SecureRandom()));
        pss.BlockUpdate(attributes, 0, attributes.Length);

        AssertRefused(path, signer, Hex(pss.GenerateSignature()), SignatureInvalidReason.AlgorithmUnsupported);
    }

    [Fact]
    public void Ecdsa_signature_for_an_rsa_certificate_is_refused()
    {
        var signer = NewRsaSigner();
        var ec = NewEcSigner();
        var (path, attributes) = Presign(signer);

        // Wrong algorithm for the certificate's key: the length alone already rules it out.
        AssertRefused(path, signer, Hex(Sign("SHA256withECDSA", ec.PrivateKey, attributes)), SignatureInvalidReason.Malformed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("zz")]
    public void Empty_or_non_hex_signature_is_refused(string signedHash)
    {
        var signer = NewRsaSigner();
        var (path, _) = Presign(signer);

        AssertRefused(path, signer, signedHash, SignatureInvalidReason.Malformed);
    }

    [Fact]
    public void Refused_signature_leaves_the_presigned_file_in_place()
    {
        var signer = NewRsaSigner();
        var (path, attributes) = Presign(signer);
        var before = File.ReadAllBytes(path);

        AssertRefused(path, signer, Hex(Sign("SHA256withRSA", GenerateRsa().Private, attributes)), SignatureInvalidReason.Mismatch);

        // The retry with the right signature still works on the same presign.
        Assert.Equal(before, File.ReadAllBytes(path));
        AssertVerifies(Finish(path, signer, Sign("SHA256withRSA", signer.PrivateKey, attributes)));
    }
}
