using DotNetSigningServer.Models;
using iText.Bouncycastle.X509;
using iText.Commons.Bouncycastle.Cert;
using iText.Forms.Fields.Properties;
using iText.Forms.Form.Element;
using iText.IO.Image;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Filespec;
using iText.Signatures;
using Org.BouncyCastle.X509;
using DotNetSigningServer.ExternalSignatures;
using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using System.Collections.Generic;
using System.Linq;

using IOPath = System.IO.Path;


namespace DotNetSigningServer.Services
{
    public class PdfSigningService
    {
        private const string DEFAULT_FIELD_NAME = "Signature1";
        private readonly TimestampAuthorityOptions _tsaOptions;

        public PdfSigningService(IOptions<TimestampAuthorityOptions> tsaOptions)
        {
            _tsaOptions = tsaOptions?.Value ?? new TimestampAuthorityOptions();
        }
        public (string PresignedPdfPath, string HashToSign) HandlePreSign(PreSignInput input, string fieldName)
        {
            byte[] originalPdf = Convert.FromBase64String(input.PdfContent);
            var preSignContainer = new DigestCalcBlankSigner(PdfName.Adobe_PPKLite, PdfName.Adbe_pkcs7_detached);
            var chain = LoadCertificatesFromPemString(input.CertificatePem);
            preSignContainer.SetChain(chain);
            fieldName = EnsureFieldName(fieldName);

            byte[] pdfWithPlaceholder = CreatePreSignedPdf(
                originalPdf,
                preSignContainer,
                fieldName,
                input.SignRect,
                input.Reason,
                input.Location,
                input.SignPageNumber,
                input.SignImageContent,
                chain);

            string preSignedPdfPath = IOPath.Combine(IOPath.GetTempPath(), $"presigned_{Guid.NewGuid():N}.pdf");
            File.WriteAllBytes(preSignedPdfPath, pdfWithPlaceholder);

            string hashToSign = BitConverter.ToString(preSignContainer.GetDocBytesHash()).Replace("-", "").ToLowerInvariant();
            return (preSignedPdfPath, hashToSign);
        }

        public string HandleSign(SignInput input, string presignedPdfPath, string certificatePem, string fieldName, string? tsaUrl = null, string? tsaUsername = null, string? tsaPassword = null)
        {
            if (!File.Exists(presignedPdfPath))
            {
                throw new FileNotFoundException("Pre-signed PDF not found.", presignedPdfPath);
            }

            byte[] preSignedPdf = File.ReadAllBytes(presignedPdfPath);
            ITSAClient? tsaClient = CreateTsaClient(tsaUrl, tsaUsername, tsaPassword);
            var chain = LoadCertificatesFromPemString(certificatePem);
            byte[] signatureBytes = HexStringToByteArray(input.SignedHash);
            fieldName = EnsureFieldName(fieldName);
            byte[] fullySignedPdf = InjectFinalSignature(preSignedPdf, signatureBytes, chain, fieldName, tsaClient);

            return Convert.ToBase64String(fullySignedPdf);
        }

        public string SignWithPfx(PfxSignInput input)
        {
            var (chain, privateKey) = LoadFromPfx(input.PfxContent, input.PfxPassword);

            byte[] originalPdf = Convert.FromBase64String(input.PdfContent);
            var preSignContainer = new DigestCalcBlankSigner(PdfName.Adobe_PPKLite, PdfName.Adbe_pkcs7_detached);
            preSignContainer.SetChain(chain);
            string fieldName = EnsureFieldName(input.FieldName, $"Signature_{Guid.NewGuid():N}");

            byte[] pdfWithPlaceholder = CreatePreSignedPdf(
                originalPdf,
                preSignContainer,
                fieldName,
                input.SignRect,
                input.Reason,
                input.Location,
                input.SignPageNumber,
                input.SignImageContent,
                chain);

            byte[] signatureBytes = SignAuthenticatedAttributes(preSignContainer.GetDocBytesHash(), privateKey);
            ITSAClient? tsaClient = CreateTsaClient();
            byte[] fullySignedPdf = InjectFinalSignature(pdfWithPlaceholder, signatureBytes, chain, fieldName, tsaClient);

            return Convert.ToBase64String(fullySignedPdf);
        }

        public string ApplyDocumentTimestamp(DocumentTimestampInput input)
        {
            ITSAClient? tsaClient = CreateTsaClient(input.TsaUrl, input.TsaUsername, input.TsaPassword);
            if (tsaClient == null)
            {
                throw new InvalidOperationException("Timestamp authority must be configured to apply document timestamps.");
            }

            string fieldName = EnsureFieldName(input.FieldName, $"Timestamp_{Guid.NewGuid():N}");
            using var msIn = new MemoryStream(Convert.FromBase64String(input.PdfContent));
            using var msOut = new MemoryStream();
            var reader = new PdfReader(msIn);
            var signer = new PdfSigner(reader, msOut, new StampingProperties().UseAppendMode());

            SignerProperties signerProperties = BuildSignerProperties(
                fieldName,
                input.SignRect,
                input.Reason,
                input.Location,
                input.SignPageNumber,
                input.SignImageContent,
                null);

            signer.SetSignerProperties(signerProperties);
            signer.Timestamp(tsaClient, fieldName);

            return Convert.ToBase64String(msOut.ToArray());
        }

        public string AddAttachment(AddAttachmentInput input)
        {
            if (string.IsNullOrWhiteSpace(input.FileName))
            {
                throw new ArgumentException("A filename is required to attach a file to the PDF.", nameof(input.FileName));
            }

            byte[] pdfBytes = Convert.FromBase64String(input.PdfContent);
            byte[] attachmentBytes = Convert.FromBase64String(input.AttachmentContent);

            using var msIn = new MemoryStream(pdfBytes);
            using var msOut = new MemoryStream();

            var reader = new PdfReader(msIn);
            var writer = new PdfWriter(msOut);
            var pdfDoc = new PdfDocument(reader, writer, new StampingProperties().UseAppendMode());

            var afRelationship = PdfName.Unspecified;
            PdfFileSpec fileSpec = string.IsNullOrWhiteSpace(input.MimeType)
                ? PdfFileSpec.CreateEmbeddedFileSpec(pdfDoc, attachmentBytes, input.Description, input.FileName, afRelationship)
                : PdfFileSpec.CreateEmbeddedFileSpec(
                    pdfDoc,
                    attachmentBytes,
                    input.Description,
                    input.FileName,
                    new PdfName(input.MimeType),
                    null,
                    afRelationship);

            pdfDoc.AddFileAttachment(input.FileName, fileSpec);
            pdfDoc.Close();

            return Convert.ToBase64String(msOut.ToArray());
        }

        private static byte[] CreatePreSignedPdf(
            byte[] originalPdf,
            DigestCalcBlankSigner container,
            string fieldName,
            SignRect signRect,
            string reason,
            string location,
            int pageNumber,
            string? signImageContent,
            IX509Certificate[] chain)
        {
            using var msIn = new MemoryStream(originalPdf);
            using var msOut = new MemoryStream();

            var reader = new PdfReader(msIn);
            var signer = new PdfSigner(reader, msOut, new StampingProperties().UseAppendMode());

            SignerProperties signerProperties = BuildSignerProperties(
                fieldName,
                signRect,
                reason,
                location,
                pageNumber,
                signImageContent,
                chain);

            signer.SetSignerProperties(signerProperties);
            // Reserve extra space so the deferred signature can include TSA timestamp tokens when present.
            signer.SignExternalContainer(container, 32768);

            return msOut.ToArray();
        }

        private static byte[] InjectFinalSignature(byte[] pdfWithPlaceholder, byte[] signatureBytes, IX509Certificate[] chain, string fieldName, ITSAClient? tsaClient)
        {
            using var msIn = new MemoryStream(pdfWithPlaceholder);
            using var msOut = new MemoryStream();

            var reader = new PdfReader(msIn);
            IExternalSignatureContainer external = new ExternalSignatureContainer(chain, signatureBytes, tsaClient);

            PdfSigner.SignDeferred(reader, fieldName, msOut, external);
            // PdfSigner.SignDeferred(new PdfDocument(reader, new PdfWriter(msOut), new StampingProperties().UseAppendMode()), FIELD_NAME, msOut, external);

            return msOut.ToArray();
        }

        private ITSAClient? CreateTsaClient(string? urlOverride = null, string? usernameOverride = null, string? passwordOverride = null)
        {
            string? url = !string.IsNullOrWhiteSpace(urlOverride) ? urlOverride : _tsaOptions?.Url;
            string? username = !string.IsNullOrWhiteSpace(usernameOverride) ? usernameOverride : _tsaOptions?.Username;
            string? password = !string.IsNullOrWhiteSpace(passwordOverride) ? passwordOverride : _tsaOptions?.Password;

            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            return new TSAClientBouncyCastle(url, username, password);
        }


        private static SignerProperties BuildSignerProperties(
            string fieldName,
            SignRect signRect,
            string reason,
            string location,
            int pageNumber,
            string? signImageContent,
            IX509Certificate[]? chain)
        {
            SignerProperties signerProperties = new SignerProperties().SetFieldName(fieldName);

            var cn = chain?.FirstOrDefault()?.GetSubjectDN()?.ToString()?.Split(",")[0].Split("=")[1];

            SignatureFieldAppearance appearance = new SignatureFieldAppearance(SignerProperties.IGNORED_ID);
            var reasonLine =
                string.IsNullOrWhiteSpace(reason)
                    ? (string.IsNullOrWhiteSpace(cn) ? string.Empty : $"Signed by {cn}")
                    : (string.IsNullOrWhiteSpace(cn) ? $"Reason: {reason}" : $"Signed by {cn}\nReason: {reason}");
            var appearanceText = new SignedAppearanceText().SetReasonLine(reasonLine);

            if (!string.IsNullOrEmpty(signImageContent))
            {
                byte[] image = Convert.FromBase64String(signImageContent);
                appearance.SetContent(appearanceText, ImageDataFactory.Create(image));
            }
            else
            {
                appearance.SetContent(appearanceText);
            }

            signerProperties
                .SetPageRect(new Rectangle(signRect.X, signRect.Y, signRect.Width, signRect.Height))
                .SetReason(reason)
                .SetLocation(location)
                .SetPageNumber(pageNumber)
                .SetCertificationLevel(AccessPermissions.UNSPECIFIED)
                .SetFieldName(fieldName)
                .SetSignatureAppearance(appearance);

            return signerProperties;
        }

        private static (IX509Certificate[] Chain, ICipherParameters PrivateKey) LoadFromPfx(string pfxContent, string password)
        {
            byte[] pfxBytes = Convert.FromBase64String(pfxContent);
            using var ms = new MemoryStream(pfxBytes);
            var store = new Pkcs12StoreBuilder().Build();
            store.Load(ms, (password ?? string.Empty).ToCharArray());

            string? alias = store.Aliases.Cast<string>().FirstOrDefault(store.IsKeyEntry);
            if (alias == null)
            {
                throw new InvalidOperationException("No private key entry found in the provided PFX file.");
            }

            var keyEntry = store.GetKey(alias);
            var certChain = store.GetCertificateChain(alias) ?? Array.Empty<X509CertificateEntry>();

            IX509Certificate[] chain = certChain
                .Select(entry => (IX509Certificate)new X509CertificateBC(entry.Certificate))
                .ToArray();

            return (chain, keyEntry.Key);
        }

        private static byte[] SignAuthenticatedAttributes(byte[] authenticatedAttributes, ICipherParameters privateKey)
        {
            var signer = SignerUtilities.GetSigner("SHA256withRSA");
            signer.Init(true, privateKey);
            signer.BlockUpdate(authenticatedAttributes, 0, authenticatedAttributes.Length);
            return signer.GenerateSignature();
        }

        private static string EnsureFieldName(string? candidate, string? fallback = null)
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

        private static IX509Certificate[] LoadCertificatesFromPemString(string pem)
        {
            using (var reader = new StringReader(pem))
            {
                var pemReader = new Org.BouncyCastle.OpenSsl.PemReader(reader);
                var certs = new List<IX509Certificate>();
                Console.Out.WriteLine(certs.ToArray());
                object? readObject;
                while ((readObject = pemReader.ReadObject()) != null)
                {

                    IX509Certificate cert = new X509CertificateBC((X509Certificate)readObject);
                    certs.Add(cert);

                }
                return certs.ToArray();
            }
        }

        private static byte[] HexStringToByteArray(string hex)
        {
            if (hex.Length % 2 != 0) throw new ArgumentException("Hex string must have an even length.");
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < hex.Length; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }
            return bytes;
        }
    }
}
