using DotNetSigningServer.Exceptions;
using iText.Commons.Bouncycastle.Cert;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace DotNetSigningServer.Services
{
    /// <summary>Why a client's signature was refused; sent as <c>reason</c> next to <c>SIGNATURE_INVALID</c>.</summary>
    public static class SignatureInvalidReason
    {
        /// <summary>Not hex, empty, or not a strict DER ECDSA value.</summary>
        public const string Malformed = "malformed";

        /// <summary>Well formed, but does not verify with the certificate's key over the presigned data.</summary>
        public const string Mismatch = "mismatch";

        /// <summary>A key or scheme the signing path cannot embed (RSA-PSS, EdDSA, …).</summary>
        public const string AlgorithmUnsupported = "algorithm_unsupported";
    }

    /// <summary>The client's signature does not belong to the presign (#81); nothing was signed or charged.</summary>
    public sealed class SignatureInvalidException : ApiValidationException
    {
        public const string ErrorCode = "SIGNATURE_INVALID";

        public string Reason { get; }

        public SignatureInvalidException(string reason, string detail)
            : base(ErrorCode, $"{reason}: {detail}")
        {
            Reason = reason;
        }
    }

    /// <summary>
    /// Verifies the externally made signature against the signed attributes before it is embedded (#81).
    /// Supported: RSA PKCS#1 v1.5 and ECDSA (DER), both with SHA-256, as the presign fixes.
    /// </summary>
    public static class ClientSignatureVerifier
    {
        /// <returns>The algorithm name iText expects for the CMS SignerInfo (<c>RSA</c> or <c>ECDSA</c>).</returns>
        public static string Verify(byte[] signedAttributes, byte[] signature, IX509Certificate signingCertificate)
        {
            if (signature.Length == 0)
            {
                throw new SignatureInvalidException(SignatureInvalidReason.Malformed, "empty signature");
            }

            X509Certificate certificate;
            try
            {
                certificate = new X509CertificateParser().ReadCertificate(signingCertificate.GetEncoded());
            }
            catch (Exception ex)
            {
                throw new SignatureInvalidException(SignatureInvalidReason.AlgorithmUnsupported, $"certificate unreadable ({ex.GetType().Name})");
            }

            var keyAlgorithm = certificate.SubjectPublicKeyInfo.Algorithm.Algorithm;
            AsymmetricKeyParameter key;
            try
            {
                key = certificate.GetPublicKey();
            }
            catch (Exception)
            {
                throw new SignatureInvalidException(SignatureInvalidReason.AlgorithmUnsupported, $"key algorithm {keyAlgorithm.Id}");
            }

            return key switch
            {
                // A PSS-only key (id-RSASSA-PSS) cannot be embedded as PKCS#1 v1.5.
                RsaKeyParameters when keyAlgorithm.Equals(PkcsObjectIdentifiers.IdRsassaPss) =>
                    throw new SignatureInvalidException(SignatureInvalidReason.AlgorithmUnsupported, "RSA-PSS key"),
                RsaKeyParameters rsa => VerifyRsa(signedAttributes, signature, rsa),
                ECPublicKeyParameters ec => VerifyEcdsa(signedAttributes, signature, ec),
                _ => throw new SignatureInvalidException(SignatureInvalidReason.AlgorithmUnsupported, $"key algorithm {keyAlgorithm.Id}"),
            };
        }

        private static string VerifyRsa(byte[] data, byte[] signature, RsaKeyParameters key)
        {
            if (signature.Length != (key.Modulus.BitLength + 7) / 8)
            {
                throw new SignatureInvalidException(SignatureInvalidReason.Malformed, "length does not match the RSA modulus");
            }

            if (Check(SignerUtilities.GetSigner("SHA-256withRSA"), key, data, signature)) return "RSA";

            // Told apart only to say clearly that PSS is not supported end to end.
            var pss = new PssSigner(new RsaBlindedEngine(), new Sha256Digest(), 32);
            if (Check(pss, key, data, signature))
            {
                throw new SignatureInvalidException(SignatureInvalidReason.AlgorithmUnsupported, "RSA-PSS signature");
            }
            throw new SignatureInvalidException(SignatureInvalidReason.Mismatch, "RSA signature does not verify");
        }

        private static string VerifyEcdsa(byte[] data, byte[] signature, ECPublicKeyParameters key)
        {
            if (!IsStrictDerEcdsa(signature))
            {
                // Raw r‖s (WebCrypto) must be converted to DER before it reaches us.
                throw new SignatureInvalidException(SignatureInvalidReason.Malformed, "ECDSA signature is not strict DER");
            }

            if (Check(SignerUtilities.GetSigner("SHA-256withECDSA"), key, data, signature)) return "ECDSA";
            throw new SignatureInvalidException(SignatureInvalidReason.Mismatch, "ECDSA signature does not verify");
        }

        private static bool Check(ISigner signer, AsymmetricKeyParameter key, byte[] data, byte[] signature)
        {
            try
            {
                signer.Init(false, key);
                signer.BlockUpdate(data, 0, data.Length);
                return signer.VerifySignature(signature);
            }
            catch (Exception ex) when (ex is CryptoException or ArgumentException or InvalidOperationException or IOException)
            {
                return false;
            }
        }

        /// <summary>Exactly <c>SEQUENCE { INTEGER r, INTEGER s }</c> in DER, nothing after it.</summary>
        private static bool IsStrictDerEcdsa(byte[] signature)
        {
            try
            {
                var sequence = Asn1Sequence.GetInstance(Asn1Object.FromByteArray(signature));
                if (sequence.Count != 2) return false;
                var r = DerInteger.GetInstance(sequence[0]).Value;
                var s = DerInteger.GetInstance(sequence[1]).Value;
                if (r.SignValue <= 0 || s.SignValue <= 0) return false;
                return sequence.GetEncoded(Asn1Encodable.Der).AsSpan().SequenceEqual(signature);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
