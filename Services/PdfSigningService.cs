using DotNetSigningServer.Models;
using iText.Commons.Bouncycastle.Cert;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Filespec;
using iText.Signatures;
using DotNetSigningServer.ExternalSignatures;
using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto;

using IOPath = System.IO.Path;


namespace DotNetSigningServer.Services
{
    public class PdfSigningService
    {
        private readonly TimestampAuthorityOptions _tsaOptions;
        private readonly EvidenceOptions _evidenceOptions;
        private readonly PdfVisualSigningService _visualSigningService;

        public PdfSigningService(
            IOptions<TimestampAuthorityOptions> tsaOptions,
            IOptions<SealOptions>? sealOptions = null,
            IOptions<EvidenceOptions>? evidenceOptions = null,
            PdfVisualSigningService? visualSigningService = null)
        {
            _tsaOptions = tsaOptions?.Value ?? new TimestampAuthorityOptions();
            _evidenceOptions = evidenceOptions?.Value ?? new EvidenceOptions();
            _visualSigningService = visualSigningService ?? new PdfVisualSigningService();
        }

        public (string PresignedPdfPath, string HashToSign) HandlePreSign(PreSignInput input, string fieldName)
        {
            byte[] originalPdf = Convert.FromBase64String(input.PdfContent);

            // Add verification metadata/QR page before signing
            if (!string.IsNullOrWhiteSpace(input.VerificationUrl) && input.VerificationMode != "disabled")
            {
                originalPdf = PdfVerificationService.AddVerification(
                    originalPdf, input.VerificationUrl, input.VerificationMode ?? "disabled");
            }
            var preSignContainer = new DigestCalcBlankSigner(PdfName.Adobe_PPKLite, PdfName.Adbe_pkcs7_detached);
            var chain = PdfCryptoHelper.LoadCertificatesFromPemString(input.CertificatePem);
            preSignContainer.SetChain(chain);
            fieldName = PdfCryptoHelper.EnsureFieldName(fieldName);

            byte[] pdfWithPlaceholder = CreatePreSignedPdf(
                originalPdf,
                preSignContainer,
                fieldName,
                input.SignRect,
                input.Reason,
                input.Location,
                input.SignPageNumber,
                input.SignImageContent,
                chain,
                input.Appearance,
                input.StampImageContent,
                input.BackgroundImageContent,
                input.CompanyLogoContent,
                visible: true,
                designWidth: input.DesignWidth,
                designHeight: input.DesignHeight,
                autoHeight: input.AutoHeight,
                signerNameOverride: input.SignerName);

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
            ITSAClient? tsaClient = PdfCryptoHelper.CreateTsaClient(_tsaOptions, tsaUrl, tsaUsername, tsaPassword);
            var chain = PdfCryptoHelper.LoadCertificatesFromPemString(certificatePem);
            byte[] signatureBytes = PdfCryptoHelper.HexStringToByteArray(input.SignedHash);
            fieldName = PdfCryptoHelper.EnsureFieldName(fieldName);
            byte[] fullySignedPdf = InjectFinalSignature(preSignedPdf, signatureBytes, chain, fieldName, tsaClient);

            return Convert.ToBase64String(fullySignedPdf);
        }

        public string SignWithPfx(PfxSignInput input)
        {
            var (chain, privateKey) = PdfCryptoHelper.LoadFromPfx(input.PfxContent, input.PfxPassword);
            byte[] originalPdf = Convert.FromBase64String(input.PdfContent);
            byte[] fullySignedPdf = SignPdfWithKeyPair(
                originalPdf,
                chain,
                privateKey,
                PdfCryptoHelper.EnsureFieldName(input.FieldName, $"Signature_{Guid.NewGuid():N}"),
                input.SignRect,
                input.Reason,
                input.Location,
                input.SignPageNumber,
                input.SignImageContent,
                input.Appearance,
                input.StampImageContent,
                input.BackgroundImageContent,
                input.CompanyLogoContent,
                visible: true,
                tsaUrl: null,
                tsaUsername: null,
                tsaPassword: null,
                designWidth: input.DesignWidth,
                designHeight: input.DesignHeight,
                autoHeight: input.AutoHeight,
                signerNameOverride: input.SignerName);

            return Convert.ToBase64String(fullySignedPdf);
        }

        public string ApplyDocumentTimestamp(DocumentTimestampInput input)
        {
            ITSAClient? tsaClient = PdfCryptoHelper.CreateTsaClient(_tsaOptions, input.TsaUrl, input.TsaUsername, input.TsaPassword);
            if (tsaClient == null)
            {
                throw new InvalidOperationException("Timestamp authority must be configured to apply document timestamps.");
            }

            string fieldName = PdfCryptoHelper.EnsureFieldName(input.FieldName, $"Timestamp_{Guid.NewGuid():N}");
            using var msIn = new MemoryStream(Convert.FromBase64String(input.PdfContent));
            using var msOut = new MemoryStream();
            var reader = new PdfReader(msIn);
            var signer = new PdfSigner(reader, msOut, new StampingProperties().UseAppendMode());

            int resolvedPage = input.SignPageNumber <= 0 ? 1 : input.SignPageNumber;
            var signerPageSize = signer.GetDocument().GetPage(resolvedPage).GetPageSize();

            SignerProperties signerProperties = PdfVisualSigningService.BuildSignerProperties(
                fieldName,
                input.SignRect,
                input.Reason,
                input.Location,
                input.SignPageNumber,
                input.SignImageContent,
                null,
                input.Appearance,
                input.StampImageContent,
                input.BackgroundImageContent,
                input.CompanyLogoContent,
                visible: true,
                designWidth: input.DesignWidth,
                designHeight: input.DesignHeight,
                autoHeight: input.AutoHeight,
                pageSize: signerPageSize,
                signerNameOverride: input.SignerName);

            signer.SetSignerProperties(signerProperties);
            signer.Timestamp(tsaClient, fieldName);

            return Convert.ToBase64String(msOut.ToArray());
        }

        public string ApplyVisualSign(VisualSignInput input)
        {
            return _visualSigningService.ApplyVisualSign(input);
        }

        public string AddAttachment(AddAttachmentInput input)
        {
            if (string.IsNullOrWhiteSpace(input.FileName))
            {
                throw new ArgumentException("A filename is required to attach a file to the PDF.", nameof(input.FileName));
            }

            byte[] pdfBytes = Convert.FromBase64String(input.PdfContent);
            byte[] attachmentBytes = Convert.FromBase64String(input.AttachmentContent);
            string attachmentFileName = input.FileName;
            string? attachmentMimeType = input.MimeType;
            if (input.UseConfiguredEncryptionCertificate && string.IsNullOrWhiteSpace(_evidenceOptions.EncryptionCertificatePem))
            {
                throw new InvalidOperationException("Evidence encryption certificate is not configured.");
            }

            string? encryptionCertificatePem = !string.IsNullOrWhiteSpace(input.EncryptForCertificatePem)
                ? input.EncryptForCertificatePem
                : input.UseConfiguredEncryptionCertificate
                    ? _evidenceOptions.EncryptionCertificatePem
                    : null;

            if (!string.IsNullOrWhiteSpace(encryptionCertificatePem))
            {
                attachmentBytes = PdfCryptoHelper.EncryptAttachmentPayload(
                    attachmentBytes,
                    encryptionCertificatePem,
                    input.CompressBeforeEncrypt);

                attachmentMimeType = string.IsNullOrWhiteSpace(attachmentMimeType)
                    ? _evidenceOptions.MimeType
                    : attachmentMimeType;
            }

            using var msIn = new MemoryStream(pdfBytes);
            using var msOut = new MemoryStream();

            var reader = new PdfReader(msIn);
            var writer = new PdfWriter(msOut);
            var pdfDoc = new PdfDocument(reader, writer, new StampingProperties().UseAppendMode());

            // Ensure unique attachment name -- if a file with the same name already
            // exists (e.g. multiple signers each adding evidence), append a suffix.
            var catalog = pdfDoc.GetCatalog();
            var existingNames = catalog.GetNameTree(PdfName.EmbeddedFiles);
            string uniqueFileName = attachmentFileName;
            if (existingNames != null)
            {
                var existingKeys = new HashSet<string>(
                    existingNames.GetKeys().Select(k => k.ToString()));
                if (existingKeys.Contains(uniqueFileName))
                {
                    var baseName = System.IO.Path.GetFileNameWithoutExtension(uniqueFileName);
                    var ext = System.IO.Path.GetExtension(uniqueFileName);
                    int counter = 2;
                    while (existingKeys.Contains($"{baseName}-{counter}{ext}"))
                        counter++;
                    uniqueFileName = $"{baseName}-{counter}{ext}";
                }
            }

            var afRelationship = PdfName.Unspecified;
            PdfFileSpec fileSpec = string.IsNullOrWhiteSpace(attachmentMimeType)
                ? PdfFileSpec.CreateEmbeddedFileSpec(pdfDoc, attachmentBytes, input.Description, uniqueFileName, afRelationship)
                : PdfFileSpec.CreateEmbeddedFileSpec(
                    pdfDoc,
                    attachmentBytes,
                    input.Description,
                    uniqueFileName,
                    new PdfName(attachmentMimeType),
                    null,
                    afRelationship);

            pdfDoc.AddFileAttachment(uniqueFileName, fileSpec);
            pdfDoc.Close();

            return Convert.ToBase64String(msOut.ToArray());
        }

        internal byte[] SignPdfWithKeyPair(
            byte[] originalPdf,
            IX509Certificate[] chain,
            ICipherParameters privateKey,
            string fieldName,
            SignRect signRect,
            string reason,
            string location,
            int pageNumber,
            string? signImageContent,
            SignatureAppearanceOptions? appearance,
            string? stampImageContent,
            string? backgroundImageContent,
            string? companyLogoContent,
            bool visible,
            string? tsaUrl,
            string? tsaUsername,
            string? tsaPassword,
            float? designWidth = null,
            float? designHeight = null,
            bool? autoHeight = null,
            string? signerNameOverride = null)
        {
            var preSignContainer = new DigestCalcBlankSigner(PdfName.Adobe_PPKLite, PdfName.Adbe_pkcs7_detached);
            preSignContainer.SetChain(chain);

            byte[] pdfWithPlaceholder = CreatePreSignedPdf(
                originalPdf,
                preSignContainer,
                fieldName,
                signRect,
                reason,
                location,
                pageNumber,
                signImageContent,
                chain,
                appearance,
                stampImageContent,
                backgroundImageContent,
                companyLogoContent,
                visible,
                designWidth,
                designHeight,
                autoHeight,
                signerNameOverride);

            byte[] signatureBytes = PdfCryptoHelper.SignAuthenticatedAttributes(preSignContainer.GetDocBytesHash(), privateKey);
            ITSAClient? tsaClient = PdfCryptoHelper.CreateTsaClient(_tsaOptions, tsaUrl, tsaUsername, tsaPassword);
            return InjectFinalSignature(pdfWithPlaceholder, signatureBytes, chain, fieldName, tsaClient);
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
            IX509Certificate[] chain,
            SignatureAppearanceOptions? appearance = null,
            string? stampImageContent = null,
            string? backgroundImageContent = null,
            string? companyLogoContent = null,
            bool visible = true,
            float? designWidth = null,
            float? designHeight = null,
            bool? autoHeight = null,
            string? signerNameOverride = null)
        {
            using var msIn = new MemoryStream(originalPdf);
            using var msOut = new MemoryStream();

            var reader = new PdfReader(msIn);
            var signer = new PdfSigner(reader, msOut, new StampingProperties().UseAppendMode());

            int resolvedPage = pageNumber <= 0 ? 1 : pageNumber;
            var signerPageSize = signer.GetDocument().GetPage(resolvedPage).GetPageSize();

            SignerProperties signerProperties = PdfVisualSigningService.BuildSignerProperties(
                fieldName,
                signRect,
                reason,
                location,
                pageNumber,
                signImageContent,
                chain,
                appearance,
                stampImageContent,
                backgroundImageContent,
                companyLogoContent,
                visible,
                designWidth,
                designHeight,
                autoHeight,
                pageSize: signerPageSize,
                signerNameOverride: signerNameOverride);

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

            return msOut.ToArray();
        }
    }
}
