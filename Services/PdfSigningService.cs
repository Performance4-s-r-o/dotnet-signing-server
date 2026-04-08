using DotNetSigningServer.Models;
using iText.Bouncycastle.X509;
using iText.Commons.Bouncycastle.Cert;
using iText.Forms.Fields.Properties;
using iText.Forms.Form.Element;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.IO.Font.Constants;
using iText.Kernel.Pdf.Filespec;
using iText.Kernel.Pdf.Xobject;
using iText.Signatures;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.Layout.Borders;
using Org.BouncyCastle.X509;
using DotNetSigningServer.ExternalSignatures;
using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using IOPath = System.IO.Path;


namespace DotNetSigningServer.Services
{
    public class PdfSigningService
    {
        private const string DEFAULT_FIELD_NAME = "Signature1";
        private readonly TimestampAuthorityOptions _tsaOptions;
        private readonly SealOptions _sealOptions;
        private readonly EvidenceOptions _evidenceOptions;

        public PdfSigningService(
            IOptions<TimestampAuthorityOptions> tsaOptions,
            IOptions<SealOptions>? sealOptions = null,
            IOptions<EvidenceOptions>? evidenceOptions = null)
        {
            _tsaOptions = tsaOptions?.Value ?? new TimestampAuthorityOptions();
            _sealOptions = sealOptions?.Value ?? new SealOptions();
            _evidenceOptions = evidenceOptions?.Value ?? new EvidenceOptions();
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
                chain,
                input.Appearance,
                input.StampImageContent,
                input.BackgroundImageContent,
                input.CompanyLogoContent);

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
            byte[] fullySignedPdf = SignPdfWithKeyPair(
                originalPdf,
                chain,
                privateKey,
                EnsureFieldName(input.FieldName, $"Signature_{Guid.NewGuid():N}"),
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
                tsaPassword: null);

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
                null,
                input.Appearance,
                input.StampImageContent,
                input.BackgroundImageContent,
                input.CompanyLogoContent);

            signer.SetSignerProperties(signerProperties);
            signer.Timestamp(tsaClient, fieldName);

            return Convert.ToBase64String(msOut.ToArray());
        }

        public string ApplyVisualSign(VisualSignInput input)
        {
            byte[] pdfBytes = Convert.FromBase64String(input.PdfContent);

            using var msIn = new MemoryStream(pdfBytes);
            using var msOut = new MemoryStream();

            var reader = new PdfReader(msIn);
            var writer = new PdfWriter(msOut);
            var pdfDoc = new PdfDocument(reader, writer, new StampingProperties().UseAppendMode());

            int pageNumber = input.SignPageNumber <= 0 ? 1 : input.SignPageNumber;
            var page = pdfDoc.GetPage(pageNumber);
            var pageSize = page.GetPageSize();

            float x = input.SignRect.X;
            float y = input.SignRect.Y;
            float width = input.SignRect.Width;
            float height = input.SignRect.Height;

            var hasSignImage = !string.IsNullOrEmpty(input.SignImageContent);
            var hasCompanyLogo = !string.IsNullOrEmpty(input.CompanyLogoContent);
            var hasStamp = !string.IsNullOrEmpty(input.StampImageContent);
            var hasBgImage = !string.IsNullOrEmpty(input.BackgroundImageContent);

            var appearanceOptions = input.Appearance ?? new SignatureAppearanceOptions();
            var showReason = appearanceOptions.ShowReason;
            var showDate = appearanceOptions.ShowDate;

            DeviceRgb? fgColor = !string.IsNullOrWhiteSpace(appearanceOptions.ForegroundColor)
                ? ParseHexColor(appearanceOptions.ForegroundColor!) : null;
            DeviceRgb? bgColor = !string.IsNullOrWhiteSpace(appearanceOptions.BackgroundColor)
                ? ParseHexColor(appearanceOptions.BackgroundColor!) : null;

            PdfFont? font = null;
            if (!string.IsNullOrWhiteSpace(appearanceOptions.FontFamily))
            {
                var fontName = appearanceOptions.FontFamily switch
                {
                    "Times" => StandardFonts.TIMES_ROMAN,
                    "Courier" => StandardFonts.COURIER,
                    _ => StandardFonts.HELVETICA,
                };
                font = PdfFontFactory.CreateFont(fontName);
            }

            float? fontSize = appearanceOptions.FontSize;

            var rect = new Rectangle(x, y, width, height);
            var canvas = new Canvas(new PdfCanvas(page), rect);

            if (hasStamp || hasBgImage || hasCompanyLogo)
            {
                // Full layout: 3-column table matching BuildSignerProperties pattern
                var div = new Div()
                    .SetWidth(UnitValue.CreatePercentValue(100))
                    .SetHeight(UnitValue.CreatePercentValue(100));

                if (hasBgImage)
                {
                    var bgData = ImageDataFactory.Create(Convert.FromBase64String(input.BackgroundImageContent!));
                    var bgSize = new BackgroundSize();
                    bgSize.SetBackgroundSizeToContain();
                    div.SetBackgroundImage(new BackgroundImage.Builder()
                        .SetImage(new PdfImageXObject(bgData))
                        .SetBackgroundSize(bgSize)
                        .Build());
                }
                else if (bgColor != null)
                {
                    div.SetBackgroundColor(bgColor);
                }

                var table = new Table(new float[] { 1, 2, 1 })
                    .SetWidth(UnitValue.CreatePercentValue(100))
                    .UseAllAvailableWidth();

                // Left: signature image + company logo
                var leftCell = new Cell().SetBorder(Border.NO_BORDER).SetVerticalAlignment(VerticalAlignment.MIDDLE);
                if (hasSignImage)
                {
                    var img = new Image(ImageDataFactory.Create(Convert.FromBase64String(input.SignImageContent!)));
                    img.SetAutoScale(true);
                    leftCell.Add(img);
                }
                if (hasCompanyLogo)
                {
                    var logoImg = new Image(ImageDataFactory.Create(Convert.FromBase64String(input.CompanyLogoContent!)));
                    logoImg.SetAutoScale(true);
                    leftCell.Add(logoImg);
                }
                table.AddCell(leftCell);

                // Center: text lines
                var centerCell = new Cell().SetBorder(Border.NO_BORDER).SetVerticalAlignment(VerticalAlignment.MIDDLE);
                var lines = BuildVisualSignTextLines(input, appearanceOptions);
                foreach (var line in lines)
                {
                    var p = new Paragraph(line);
                    if (font != null) p.SetFont(font);
                    if (fgColor != null) p.SetFontColor(fgColor);
                    if (fontSize.HasValue) p.SetFontSize(fontSize.Value);
                    centerCell.Add(p);
                }
                table.AddCell(centerCell);

                // Right: stamp image
                var rightCell = new Cell().SetBorder(Border.NO_BORDER).SetVerticalAlignment(VerticalAlignment.MIDDLE);
                if (hasStamp)
                {
                    var stampImg = new Image(ImageDataFactory.Create(Convert.FromBase64String(input.StampImageContent!)));
                    stampImg.SetAutoScale(true);
                    rightCell.Add(stampImg);
                }
                table.AddCell(rightCell);

                div.Add(table);
                canvas.Add(div);
            }
            else if (hasSignImage)
            {
                // Image + text layout matching digital signature appearance
                var lines = BuildVisualSignTextLines(input, appearanceOptions);

                if (lines.Count > 0)
                {
                    // 2-column table: [signature image | text lines]
                    var div = new Div()
                        .SetWidth(UnitValue.CreatePercentValue(100))
                        .SetHeight(UnitValue.CreatePercentValue(100));

                    if (bgColor != null)
                        div.SetBackgroundColor(bgColor);

                    var table = new Table(new float[] { 1, 2 })
                        .SetWidth(UnitValue.CreatePercentValue(100))
                        .UseAllAvailableWidth();

                    var imgCell = new Cell().SetBorder(Border.NO_BORDER).SetVerticalAlignment(VerticalAlignment.MIDDLE);
                    var img = new Image(ImageDataFactory.Create(Convert.FromBase64String(input.SignImageContent!)));
                    img.SetAutoScale(true);
                    imgCell.Add(img);
                    table.AddCell(imgCell);

                    var textCell = new Cell().SetBorder(Border.NO_BORDER).SetVerticalAlignment(VerticalAlignment.MIDDLE);
                    foreach (var line in lines)
                    {
                        var p = new Paragraph(line);
                        if (font != null) p.SetFont(font);
                        if (fgColor != null) p.SetFontColor(fgColor);
                        if (fontSize.HasValue) p.SetFontSize(fontSize.Value);
                        textCell.Add(p);
                    }
                    table.AddCell(textCell);

                    div.Add(table);
                    canvas.Add(div);
                }
                else
                {
                    // No text lines — just the image
                    var imgData = ImageDataFactory.Create(Convert.FromBase64String(input.SignImageContent!));
                    var img = new Image(imgData);
                    img.ScaleToFit(width, height);
                    img.SetFixedPosition(x, y);

                    canvas.Close();
                    var layoutDoc = new iText.Layout.Document(pdfDoc);
                    layoutDoc.Add(img);
                    layoutDoc.Flush();
                    pdfDoc.Close();
                    return Convert.ToBase64String(msOut.ToArray());
                }
            }
            else
            {
                // Text-only visual stamp
                var lines = BuildVisualSignTextLines(input, appearanceOptions);
                if (bgColor != null)
                {
                    var div = new Div()
                        .SetWidth(UnitValue.CreatePercentValue(100))
                        .SetHeight(UnitValue.CreatePercentValue(100))
                        .SetBackgroundColor(bgColor);
                    foreach (var line in lines)
                    {
                        var p = new Paragraph(line);
                        if (font != null) p.SetFont(font);
                        if (fgColor != null) p.SetFontColor(fgColor);
                        if (fontSize.HasValue) p.SetFontSize(fontSize.Value);
                        div.Add(p);
                    }
                    canvas.Add(div);
                }
                else
                {
                    foreach (var line in lines)
                    {
                        var p = new Paragraph(line);
                        if (font != null) p.SetFont(font);
                        if (fgColor != null) p.SetFontColor(fgColor);
                        if (fontSize.HasValue) p.SetFontSize(fontSize.Value);
                        canvas.Add(p);
                    }
                }
            }

            canvas.Close();
            pdfDoc.Close();

            return Convert.ToBase64String(msOut.ToArray());
        }

        public string ApplySeal(SealInput input)
        {
            if (!_sealOptions.Enabled)
            {
                throw new InvalidOperationException("Server-side sealing is not enabled.");
            }

            string pdfContent = input.PdfContent;

            // Add verification metadata/QR page before sealing
            if (!string.IsNullOrWhiteSpace(input.VerificationUrl) && input.VerificationMode != "disabled")
            {
                byte[] pdfBytes = Convert.FromBase64String(pdfContent);
                pdfBytes = PdfVerificationService.AddVerification(
                    pdfBytes, input.VerificationUrl, input.VerificationMode ?? "disabled", input.SignerName);
                pdfContent = Convert.ToBase64String(pdfBytes);
            }

            if (ShouldApplyVisibleOverlay(input))
            {
                pdfContent = ApplyVisualSign(new VisualSignInput
                {
                    PdfContent = pdfContent,
                    Location = input.Location,
                    Reason = input.Reason,
                    SignRect = input.SignRect,
                    SignImageContent = input.SignImageContent,
                    StampImageContent = input.StampImageContent,
                    CompanyLogoContent = input.CompanyLogoContent,
                    BackgroundImageContent = input.BackgroundImageContent,
                    SignPageNumber = input.SignPageNumber,
                    Appearance = input.Appearance,
                    TemplateId = input.TemplateId,
                    SignerName = input.SignerName,
                });
            }

            var (chain, privateKey) = LoadSealCredentials();
            byte[] originalPdf = Convert.FromBase64String(pdfContent);
            byte[] fullySignedPdf = SignPdfWithKeyPair(
                originalPdf,
                chain,
                privateKey,
                EnsureFieldName(null, $"Seal_{Guid.NewGuid():N}"),
                input.SignRect,
                string.IsNullOrWhiteSpace(input.Reason) ? _sealOptions.Reason : input.Reason,
                string.IsNullOrWhiteSpace(input.Location) ? _sealOptions.Location : input.Location,
                input.SignPageNumber,
                _sealOptions.Visible ? input.SignImageContent : null,
                _sealOptions.Visible ? input.Appearance : null,
                _sealOptions.Visible ? input.StampImageContent : null,
                _sealOptions.Visible ? input.BackgroundImageContent : null,
                _sealOptions.Visible ? input.CompanyLogoContent : null,
                visible: _sealOptions.Visible,
                tsaUrl: input.TsaUrl,
                tsaUsername: input.TsaUsername,
                tsaPassword: input.TsaPassword);

            return Convert.ToBase64String(fullySignedPdf);
        }

        private static bool ShouldApplyVisibleOverlay(SealInput input)
        {
            return !string.IsNullOrWhiteSpace(input.SignImageContent)
                || !string.IsNullOrWhiteSpace(input.StampImageContent)
                || !string.IsNullOrWhiteSpace(input.CompanyLogoContent)
                || !string.IsNullOrWhiteSpace(input.BackgroundImageContent)
                || !string.IsNullOrWhiteSpace(input.SignerName);
        }

        private byte[] SignPdfWithKeyPair(
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
            string? tsaPassword)
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
                visible);

            byte[] signatureBytes = SignAuthenticatedAttributes(preSignContainer.GetDocBytesHash(), privateKey);
            ITSAClient? tsaClient = CreateTsaClient(tsaUrl, tsaUsername, tsaPassword);
            return InjectFinalSignature(pdfWithPlaceholder, signatureBytes, chain, fieldName, tsaClient);
        }

        private (IX509Certificate[] Chain, ICipherParameters PrivateKey) LoadSealCredentials()
        {
            byte[] pfxBytes;
            if (!string.IsNullOrWhiteSpace(_sealOptions.PfxBase64))
            {
                pfxBytes = Convert.FromBase64String(_sealOptions.PfxBase64);
            }
            else if (!string.IsNullOrWhiteSpace(_sealOptions.PfxPath) && File.Exists(_sealOptions.PfxPath))
            {
                pfxBytes = File.ReadAllBytes(_sealOptions.PfxPath);
            }
            else
            {
                throw new InvalidOperationException("Seal certificate is not configured.");
            }

            return LoadFromPfxBytes(pfxBytes, _sealOptions.PfxPassword);
        }

        private static List<string> BuildVisualSignTextLines(VisualSignInput input, SignatureAppearanceOptions appearance)
        {
            var lines = new List<string>();
            if (appearance.ShowSignerName && !string.IsNullOrWhiteSpace(input.SignerName))
                lines.Add($"Signed by {input.SignerName}");
            if (appearance.ShowReason && !string.IsNullOrWhiteSpace(input.Reason))
                lines.Add($"Reason: {input.Reason}");
            if (appearance.ShowLocation && !string.IsNullOrWhiteSpace(input.Location))
                lines.Add($"Location: {input.Location}");
            if (appearance.ShowDate)
                lines.Add($"Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            if (!string.IsNullOrWhiteSpace(appearance.DescriptionText))
                lines.Add(appearance.DescriptionText!);
            return lines;
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
                attachmentBytes = EncryptAttachmentPayload(
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

            // Ensure unique attachment name — if a file with the same name already
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
            bool visible = true)
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
                chain,
                appearance,
                stampImageContent,
                backgroundImageContent,
                companyLogoContent,
                visible);

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

            // Validate user-provided TSA URLs: only allow HTTPS (or the configured default URL)
            if (!string.IsNullOrWhiteSpace(urlOverride)
                && !string.Equals(urlOverride, _tsaOptions?.Url, StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(urlOverride, UriKind.Absolute, out var parsedUri)
                    || !string.Equals(parsedUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("User-provided TSA URL must use HTTPS.");
                }
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
            IX509Certificate[]? chain,
            SignatureAppearanceOptions? appearanceOptions = null,
            string? stampImageContent = null,
            string? backgroundImageContent = null,
            string? companyLogoContent = null,
            bool visible = true)
        {
            SignerProperties signerProperties = new SignerProperties().SetFieldName(fieldName);

            signerProperties
                .SetReason(reason)
                .SetLocation(location)
                .SetCertificationLevel(AccessPermissions.UNSPECIFIED)
                .SetFieldName(fieldName);

            if (!visible)
            {
                return signerProperties;
            }

            var subjectDN = chain?.FirstOrDefault()?.GetSubjectDN()?.ToString();
            var cn = subjectDN?.Split(",").Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("CN="))?.Substring(3);

            SignatureFieldAppearance appearance = new SignatureFieldAppearance(SignerProperties.IGNORED_ID);

            // Build text lines based on appearance options (or use defaults)
            var showSignerName = appearanceOptions?.ShowSignerName ?? true;
            var showCompanyName = appearanceOptions?.ShowCompanyName ?? true;
            var showReason = appearanceOptions?.ShowReason ?? true;
            var showDate = appearanceOptions?.ShowDate ?? true;

            var lines = new List<string>();
            if (showSignerName && !string.IsNullOrWhiteSpace(cn))
                lines.Add($"Signed by {cn}");
            if (showCompanyName && !string.IsNullOrWhiteSpace(subjectDN))
            {
                var orgMatch = subjectDN!.Split(",").Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("O="));
                if (orgMatch != null)
                    lines.Add($"Company: {orgMatch.Substring(2)}");
            }
            if (showReason && !string.IsNullOrWhiteSpace(reason))
                lines.Add($"Reason: {reason}");
            if (!string.IsNullOrWhiteSpace(appearanceOptions?.DescriptionText))
                lines.Add(appearanceOptions!.DescriptionText!);

            // Resolve styling
            var hasSignImage = !string.IsNullOrEmpty(signImageContent);
            var hasCompanyLogo = !string.IsNullOrEmpty(companyLogoContent);
            var hasStamp = !string.IsNullOrEmpty(stampImageContent);
            var hasBgImage = !string.IsNullOrEmpty(backgroundImageContent);

            DeviceRgb? fgColor = !string.IsNullOrWhiteSpace(appearanceOptions?.ForegroundColor)
                ? ParseHexColor(appearanceOptions!.ForegroundColor!) : null;
            DeviceRgb? bgColor = !string.IsNullOrWhiteSpace(appearanceOptions?.BackgroundColor)
                ? ParseHexColor(appearanceOptions!.BackgroundColor!) : null;

            PdfFont? font = null;
            if (!string.IsNullOrWhiteSpace(appearanceOptions?.FontFamily))
            {
                var fontName = appearanceOptions!.FontFamily switch
                {
                    "Times" => StandardFonts.TIMES_ROMAN,
                    "Courier" => StandardFonts.COURIER,
                    _ => StandardFonts.HELVETICA,
                };
                font = PdfFontFactory.CreateFont(fontName);
            }

            float? fontSize = appearanceOptions?.FontSize;

            if (hasStamp || hasBgImage || hasCompanyLogo)
            {
                // Custom Div-based layout for stamp/background/company logo support
                var div = new Div()
                    .SetWidth(UnitValue.CreatePercentValue(100))
                    .SetHeight(UnitValue.CreatePercentValue(100));

                // Background: image takes priority over color
                if (hasBgImage)
                {
                    var bgData = ImageDataFactory.Create(Convert.FromBase64String(backgroundImageContent!));
                    var bgSize = new BackgroundSize();
                    bgSize.SetBackgroundSizeToContain();
                    div.SetBackgroundImage(new BackgroundImage.Builder()
                        .SetImage(new PdfImageXObject(bgData))
                        .SetBackgroundSize(bgSize)
                        .Build());
                }
                else if (bgColor != null)
                {
                    div.SetBackgroundColor(bgColor);
                }

                // 3-column table: [signature+logo | text | stamp]
                var table = new Table(new float[] { 1, 2, 1 })
                    .SetWidth(UnitValue.CreatePercentValue(100))
                    .UseAllAvailableWidth();

                // Left: signature image + company logo (stacked vertically)
                var leftCell = new Cell().SetBorder(Border.NO_BORDER).SetVerticalAlignment(VerticalAlignment.MIDDLE);
                if (hasSignImage)
                {
                    var img = new Image(ImageDataFactory.Create(Convert.FromBase64String(signImageContent!)));
                    img.SetAutoScale(true);
                    leftCell.Add(img);
                }
                if (hasCompanyLogo)
                {
                    var logoImg = new Image(ImageDataFactory.Create(Convert.FromBase64String(companyLogoContent!)));
                    logoImg.SetAutoScale(true);
                    leftCell.Add(logoImg);
                }
                table.AddCell(leftCell);

                // Center: text paragraphs
                var centerCell = new Cell().SetBorder(Border.NO_BORDER).SetVerticalAlignment(VerticalAlignment.MIDDLE);
                foreach (var line in lines)
                {
                    var p = new Paragraph(line);
                    if (font != null) p.SetFont(font);
                    if (fgColor != null) p.SetFontColor(fgColor);
                    if (fontSize.HasValue) p.SetFontSize(fontSize.Value);
                    centerCell.Add(p);
                }
                table.AddCell(centerCell);

                // Right: stamp image
                var rightCell = new Cell().SetBorder(Border.NO_BORDER).SetVerticalAlignment(VerticalAlignment.MIDDLE);
                if (hasStamp)
                {
                    var stampImg = new Image(ImageDataFactory.Create(Convert.FromBase64String(stampImageContent!)));
                    stampImg.SetAutoScale(true);
                    rightCell.Add(stampImg);
                }
                table.AddCell(rightCell);

                div.Add(table);
                appearance.SetContent(div);
            }
            else
            {
                // Existing path — backward compatible
                var reasonLine = string.Join("\n", lines);
                var appearanceText = new SignedAppearanceText().SetReasonLine(reasonLine);

                if (hasSignImage)
                {
                    byte[] image = Convert.FromBase64String(signImageContent!);
                    appearance.SetContent(appearanceText, ImageDataFactory.Create(image));
                }
                else
                {
                    appearance.SetContent(appearanceText);
                }

                // Apply color and font customization
                if (fgColor != null)
                    appearance.SetFontColor(fgColor);
                if (bgColor != null)
                    appearance.SetBackgroundColor(bgColor);
                if (fontSize.HasValue)
                    appearance.SetFontSize(fontSize.Value);
                if (font != null)
                    appearance.SetFont(font);
            }

            signerProperties
                .SetPageRect(new Rectangle(signRect.X, signRect.Y, signRect.Width, signRect.Height))
                .SetPageNumber(pageNumber)
                .SetSignatureAppearance(appearance);

            return signerProperties;
        }

        private static DeviceRgb ParseHexColor(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length != 6)
                return new DeviceRgb(0, 0, 0);

            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return new DeviceRgb(r, g, b);
        }

        private static (IX509Certificate[] Chain, ICipherParameters PrivateKey) LoadFromPfx(string pfxContent, string password)
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

        private static (IX509Certificate[] Chain, ICipherParameters PrivateKey) LoadFromPfxBytes(byte[] pfxBytes, string? password)
        {
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

        private static byte[] EncryptAttachmentPayload(byte[] attachmentBytes, string recipientCertificatePem, bool compressBeforeEncrypt)
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

        private static byte[] CompressBytes(byte[] bytes)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gzip.Write(bytes, 0, bytes.Length);
            }

            return output.ToArray();
        }

        private static X509Certificate LoadFirstCertificateFromPemString(string pem)
        {
            var certificates = LoadCertificatesFromPemString(pem);
            if (certificates.Length == 0)
            {
                throw new InvalidOperationException("At least one recipient certificate is required for evidence encryption.");
            }

            return ((X509CertificateBC)certificates[0]).GetCertificate();
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
                object? readObject;
                while ((readObject = pemReader.ReadObject()) != null)
                {

                    IX509Certificate cert = new X509CertificateBC((Org.BouncyCastle.X509.X509Certificate)readObject);
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
