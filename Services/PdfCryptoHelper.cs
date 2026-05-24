using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Options;
using iText.Bouncycastle.X509;
using iText.Commons.Bouncycastle.Cert;
using iText.Kernel.Colors;
using iText.Signatures;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using System.IO.Compression;
using System.Security.Cryptography;

namespace DotNetSigningServer.Services
{
    public static class PdfCryptoHelper
    {
        private const string DEFAULT_FIELD_NAME = "Signature1";

        public static (IX509Certificate[] Chain, ICipherParameters PrivateKey) LoadFromPfx(string pfxContent, string password)
        {
            byte[] pfxBytes = Convert.FromBase64String(pfxContent);
            try
            {
                return LoadFromPfxBytes(pfxBytes, password);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfxBytes);
            }
        }

        public static (IX509Certificate[] Chain, ICipherParameters PrivateKey) LoadFromPfxBytes(byte[] pfxBytes, string? password)
        {
            using var ms = new MemoryStream(pfxBytes);
            var store = new Pkcs12StoreBuilder().Build();
            store.Load(ms, (password ?? string.Empty).ToCharArray());

            string? alias = store.Aliases.Cast<string>().FirstOrDefault(store.IsKeyEntry);
            if (alias == null)
            {
                throw new ApiValidationException("PFX_NO_PRIVATE_KEY");
            }

            var keyEntry = store.GetKey(alias);
            var certChain = store.GetCertificateChain(alias) ?? Array.Empty<X509CertificateEntry>();

            IX509Certificate[] chain = certChain
                .Select(entry => (IX509Certificate)new X509CertificateBC(entry.Certificate))
                .ToArray();

            return (chain, keyEntry.Key);
        }

        public static IX509Certificate[] LoadCertificatesFromPemString(string pem)
        {
            using (var reader = new StringReader(pem))
            {
                var pemReader = new Org.BouncyCastle.OpenSsl.PemReader(reader);
                var certs = new List<IX509Certificate>();
                object? readObject;
                while ((readObject = pemReader.ReadObject()) != null)
                {

                    IX509Certificate cert = new X509CertificateBC((Org.BouncyCastle.X509.X509Certificate)readObject);
                    certs.Add(cert);

                }
                return certs.ToArray();
            }
        }

        public static X509Certificate LoadFirstCertificateFromPemString(string pem)
        {
            var certificates = LoadCertificatesFromPemString(pem);
            if (certificates.Length == 0)
            {
                throw new ApiValidationException("ENCRYPTION_NO_RECIPIENTS");
            }

            return ((X509CertificateBC)certificates[0]).GetCertificate();
        }

        public static ITSAClient? CreateTsaClient(
            TimestampAuthorityOptions? tsaOptions,
            string? urlOverride = null,
            string? usernameOverride = null,
            string? passwordOverride = null,
            bool allowDefaultFallback = true)
        {
            string? url = !string.IsNullOrWhiteSpace(urlOverride)
                ? urlOverride
                : (allowDefaultFallback ? tsaOptions?.Url : null);
            string? username = !string.IsNullOrWhiteSpace(urlOverride)
                ? usernameOverride
                : (allowDefaultFallback ? tsaOptions?.Username : null);
            string? password = !string.IsNullOrWhiteSpace(urlOverride)
                ? passwordOverride
                : (allowDefaultFallback ? tsaOptions?.Password : null);

            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            // Validate user-provided TSA URLs: only allow HTTPS (or the configured default URL)
            if (!string.IsNullOrWhiteSpace(urlOverride)
                && !string.Equals(urlOverride, tsaOptions?.Url, StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(urlOverride, UriKind.Absolute, out var parsedUri)
                    || !string.Equals(parsedUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ApiValidationException("TSA_HTTPS_REQUIRED");
                }
                // SSRF guard: a caller-supplied TSA URL must not point at an
                // internal/loopback/link-local address. Public TSAs are unaffected.
                if (ResolvesToPrivateAddress(parsedUri.DnsSafeHost))
                {
                    throw new ApiValidationException("TSA_HOST_NOT_ALLOWED");
                }
            }

            return new TSAClientBouncyCastle(url, username, password);
        }

        /// <summary>
        /// True if the host is, or DNS-resolves to, a loopback / private / link-local /
        /// unique-local address — blocked to prevent SSRF into the internal network.
        /// Fails closed (returns true) if the host cannot be resolved.
        /// </summary>
        private static bool ResolvesToPrivateAddress(string host)
        {
            System.Net.IPAddress[] addresses;
            if (System.Net.IPAddress.TryParse(host, out var literal))
            {
                addresses = new[] { literal };
            }
            else
            {
                try
                {
                    addresses = System.Net.Dns.GetHostAddresses(host);
                }
                catch
                {
                    return true; // unresolvable → treat as not allowed
                }
                if (addresses.Length == 0) return true;
            }

            foreach (var ip in addresses)
            {
                if (IsPrivateOrLocal(ip)) return true;
            }
            return false;
        }

        private static bool IsPrivateOrLocal(System.Net.IPAddress ip)
        {
            if (System.Net.IPAddress.IsLoopback(ip)) return true;

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                // 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16, 100.64.0.0/10 (CGNAT), 0.0.0.0/8
                if (b[0] == 10) return true;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                if (b[0] == 192 && b[1] == 168) return true;
                if (b[0] == 169 && b[1] == 254) return true;
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
                if (b[0] == 0) return true;
            }
            else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
                var b = ip.GetAddressBytes();
                if ((b[0] & 0xfe) == 0xfc) return true; // fc00::/7 unique-local
                // IPv4-mapped (::ffff:a.b.c.d) → re-check the embedded v4
                if (ip.IsIPv4MappedToIPv6 && IsPrivateOrLocal(ip.MapToIPv4())) return true;
            }
            return false;
        }

        public static byte[] EncryptAttachmentPayload(byte[] attachmentBytes, string recipientCertificatePem, bool compressBeforeEncrypt)
        {
            byte[] payloadBytes = compressBeforeEncrypt
                ? CompressBytes(attachmentBytes)
                : attachmentBytes;

            var recipientCertificate = LoadFirstCertificateFromPemString(recipientCertificatePem);
            var envelopeGenerator = new CmsEnvelopedDataGenerator();
            envelopeGenerator.AddKeyTransRecipient(recipientCertificate);

            var cmsData = envelopeGenerator.Generate(
                new CmsProcessableByteArray(payloadBytes),
                CmsEnvelopedDataGenerator.Aes256Cbc);

            return cmsData.GetEncoded();
        }

        public static byte[] CompressBytes(byte[] bytes)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gzip.Write(bytes, 0, bytes.Length);
            }

            return output.ToArray();
        }

        public static byte[] SignAuthenticatedAttributes(byte[] authenticatedAttributes, ICipherParameters privateKey)
        {
            var signer = SignerUtilities.GetSigner("SHA256withRSA");
            signer.Init(true, privateKey);
            signer.BlockUpdate(authenticatedAttributes, 0, authenticatedAttributes.Length);
            return signer.GenerateSignature();
        }

        public static byte[] HexStringToByteArray(string hex)
        {
            if (hex.Length % 2 != 0) throw new ArgumentException("Hex string must have an even length.");
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < hex.Length; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }
            return bytes;
        }

        public static DeviceRgb ParseHexColor(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length != 6)
                return new DeviceRgb(0, 0, 0);

            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return new DeviceRgb(r, g, b);
        }

        public static string EnsureFieldName(string? candidate, string? fallback = null)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate!;
            }

            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback!;
            }

            return DEFAULT_FIELD_NAME;
        }
    }
}
