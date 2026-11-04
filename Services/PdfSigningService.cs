using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using iText.Commons.Bouncycastle.Cert;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Filespec;
using iText.Signatures;
using DotNetSigningServer.ExternalSignatures;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.SignatureLayout;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Tsp;

using IOPath = System.IO.Path;


namespace DotNetSigningServer.Services
{
    public class PdfSigningService
    {
        private readonly EvidenceOptions _evidenceOptions;
        private readonly PdfVisualSigningService _visualSigningService;

        public PdfSigningService(
            IOptions<EvidenceOptions>? evidenceOptions = null,
            PdfVisualSigningService? visualSigningService = null)
        {
            _evidenceOptions = evidenceOptions?.Value ?? new EvidenceOptions();
            _visualSigningService = visualSigningService ?? new PdfVisualSigningService();
        }

        public (string PresignedPdfPath, string HashToSign) HandlePreSign(PreSignInput input, string fieldName) =>
            HandlePreSign(input, Convert.FromBase64String(input.PdfContent), fieldName);

        /// <summary>
        /// Presign nad bajty, které už prošly vstupní kontrolou; <c>input.PdfContent</c> se nečte.
        /// Oba kanály (JSON i binární, #74) končí tady.
        /// </summary>
        /// <param name="keepPdfA">Set when the document is PDF/A and must stay so (#184): the presigned
        /// document is checked, and one that is not is refused as <see cref="DocumentPreparation.NotPreservedCode"/>.</param>
        public (string PresignedPdfPath, string HashToSign) HandlePreSign(PreSignInput input, byte[] originalPdf, string fieldName, PdfAConformance? keepPdfA = null)
        {
            var (path, hash, _) = HandlePreSignWithStamp(input, originalPdf, fieldName, keepPdfA);
            return (path, hash);
        }

        /// <summary>Presign that also reports where the content stamp and the field went (portál #511).</summary>
        public (string PresignedPdfPath, string HashToSign, ContentStampPlacement? Placement) HandlePreSignWithStamp(
            PreSignInput input, byte[] originalPdf, string fieldName, PdfAConformance? keepPdfA = null)
        {
            if (input.PlaceBesideContentStamp && input.ContentStamp == null)
            {
                throw new ApiValidationException("CONTENT_STAMP_REQUIRED");
            }

            // Add verification metadata/QR page before signing
            if (!string.IsNullOrWhiteSpace(input.VerificationUrl) && input.VerificationMode != "disabled")
            {
                originalPdf = PdfVerificationService.AddVerification(
                    originalPdf, input.VerificationUrl, input.VerificationMode ?? "disabled");
            }

            // Stamp custom fillable field values BEFORE the placeholder + hash so they are
            // covered by this signature (tamper-evident). Mirrors the AddVerification pattern above.
            if (input.Fields is { Count: > 0 })
            {
                originalPdf = PdfTemplateService.StampTextFields(originalPdf, input.Fields);
            }

            // Image signature and the field beside it, planned before anything is written (portál #511).
            var stampPlan = input.ContentStamp == null ? null : PlanContentStamp(input, originalPdf);
            ContentStampPlacement? placement = stampPlan?.Placement;
            var signRect = stampPlan?.SignRect ?? input.SignRect;
            var signPage = stampPlan?.SignPage ?? input.SignPageNumber;
            // The computed box is final: growing it could cover the stamp.
            var autoHeight = stampPlan?.SignRect != null ? false : input.AutoHeight;

            var preSignContainer = new DigestCalcBlankSigner(PdfName.Adobe_PPKLite, PdfCryptoHelper.SignatureSubFilter);
            var chain = PdfCryptoHelper.LoadCertificatesFromPemString(input.CertificatePem);
            preSignContainer.SetChain(chain);
            fieldName = PdfCryptoHelper.EnsureFieldName(fieldName);

            // iText checks the signature appearance of a PDF/A document as it writes it; the QR page
            // and stamped fields are written without its checks and are checked afterwards.
            byte[] pdfWithPlaceholder = DocumentPreparation.KeepPdfA(keepPdfA, () => CreatePreSignedPdf(
                originalPdf,
                preSignContainer,
                fieldName,
                signRect,
                input.Reason,
                input.Location,
                signPage,
                input.SignImageContent,
                chain,
                input.Appearance,
                input.StampImageContent,
                input.BackgroundImageContent,
                input.CompanyLogoContent,
                visible: true,
                designWidth: input.DesignWidth,
                designHeight: input.DesignHeight,
                autoHeight: autoHeight,
                signerNameOverride: input.SignerName,
                stamp: stampPlan));
            DocumentPreparation.EnsurePreserved(pdfWithPlaceholder, keepPdfA);

            string preSignedPdfPath = IOPath.Combine(IOPath.GetTempPath(), $"presigned_{Guid.NewGuid():N}.pdf");
            File.WriteAllBytes(preSignedPdfPath, pdfWithPlaceholder);

            string hashToSign = BitConverter.ToString(preSignContainer.GetDocBytesHash()).Replace("-", "").ToLowerInvariant();
            return (preSignedPdfPath, hashToSign, placement);
        }

        /// <summary>Refusal of a stamp on a certified document that allows no such change (portál #511).</summary>
        public const string CertifiedNoChangesCode = "DOCUMENT_CERTIFIED_NO_CHANGES";

        /// <summary>A planned content stamp: where it lands, how it is drawn and where the field goes.</summary>
        public sealed record ContentStampPlan(
            PdfVisualSigningService.StampPlan Stamp, bool AsAnnotation, SignRect? SignRect, int? SignPage, ContentStampPlacement Placement);

        /// <summary>
        /// Where the stamp and the seal beside it go, without writing anything (portál #511). Neither
        /// may cover another signature, a stamp of an earlier signer or a box in <c>occupied</c>, and both
        /// stay inside the CropBox; otherwise <see cref="SealPlacement.NoRoomCode"/>, nothing is moved.
        /// Rects in and out are in the client frame (CropBox corner, as displayed; <see cref="PageFrame"/>).
        /// A document that is already signed gets the stamp as an annotation; a certified one that
        /// allows no annotation is refused as <see cref="CertifiedNoChangesCode"/>.
        /// </summary>
        public static ContentStampPlan PlanContentStamp(PreSignInput input, byte[] pdf)
        {
            if (input.ContentStamp == null) throw new ApiValidationException("CONTENT_STAMP_REQUIRED");
            using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));

            bool signed = new SignatureUtil(doc).GetSignatureNames().Count > 0;
            if (signed && CertificationLevel(doc) is 1 or 2)
            {
                throw new ApiValidationException(CertifiedNoChangesCode);
            }

            var stamp = PdfVisualSigningService.PlanStamp(
                input.ContentStamp.ToVisualSign(), doc, PdfVisualSigningService.CompanyOf(input.ContentStamp));
            var obstacles = SealPlacement.ObstaclesOn(doc, stamp.Page, input.Occupied);
            if (obstacles.Occupied.Any(o => SealPlacement.Overlaps(stamp.Rect, o)))
            {
                throw new ApiValidationException(SealPlacement.NoRoomCode, "the stamp covers another signature");
            }

            SignRect? signRect = null;
            int? signPage = null;
            if (input.PlaceBesideContentStamp)
            {
                var field = SealPlacement.Beside(
                    stamp.Rect, input.SignRect.Width, input.SignRect.Height, stamp.Frame.Box, obstacles)
                    ?? throw new ApiValidationException(SealPlacement.NoRoomCode, "no free spot beside the stamp");
                signRect = new SignRect { X = field.GetX(), Y = field.GetY(), Width = field.GetWidth(), Height = field.GetHeight() };
                signPage = stamp.Page;
            }

            var placement = new ContentStampPlacement(
                stamp.Page,
                new SignRect { X = stamp.Rect.GetX(), Y = stamp.Rect.GetY(), Width = stamp.Rect.GetWidth(), Height = stamp.Rect.GetHeight() },
                signRect ?? input.SignRect,
                Annotation: signed);
            return new ContentStampPlan(stamp, signed, signRect, signPage, placement);
        }

        /// <summary>DocMDP level of a certified document (1–3), null when it is not certified.</summary>
        internal static int? CertificationLevel(PdfDocument doc)
        {
            var docMdp = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Perms)?.GetAsDictionary(PdfName.DocMDP);
            if (docMdp == null) return null;
            // Certified without a readable /Reference: assume the strictest level.
            var references = docMdp.GetAsArray(PdfName.Reference);
            if (references == null) return 1;
            for (int i = 0; i < references.Size(); i++)
            {
                var reference = references.GetAsDictionary(i);
                if (reference == null || !PdfName.DocMDP.Equals(reference.GetAsName(PdfName.TransformMethod))) continue;
                // An absent /P means 2 (ISO 32000-1, 12.8.2.2).
                return reference.GetAsDictionary(PdfName.TransformParams)?.GetAsNumber(PdfName.P)?.IntValue() ?? 2;
            }
            // A /DocMDP entry without a readable reference is still a certification: assume the strict level.
            return 1;
        }

        public virtual string HandleSign(SignInput input, string presignedPdfPath, string certificatePem, string fieldName, string? tsaUrl = null, string? tsaUsername = null, string? tsaPassword = null, TsaEndpoint? tsaBackup = null)
        {
            if (!File.Exists(presignedPdfPath))
            {
                throw new ApiValidationException("PRESIGNED_NOT_FOUND");
            }

            byte[] preSignedPdf = File.ReadAllBytes(presignedPdfPath);
            ITSAClient? tsaClient = PdfCryptoHelper.CreateTsaClient(tsaUrl, tsaUsername, tsaPassword, tsaBackup);
            var chain = PdfCryptoHelper.LoadCertificatesFromPemString(certificatePem);
            byte[] signatureBytes = ParseClientSignature(input.SignedHash);
            fieldName = PdfCryptoHelper.EnsureFieldName(fieldName);
            byte[] fullySignedPdf = InjectFinalSignature(preSignedPdf, signatureBytes, chain, fieldName, tsaClient, tsaUrlForErrorContext: tsaUrl);

            return Convert.ToBase64String(fullySignedPdf);
        }

        /// <summary>The client's signature as bytes; anything that is not hex is refused like a wrong signature.</summary>
        private static byte[] ParseClientSignature(string? signedHash)
        {
            var hex = signedHash?.Trim() ?? string.Empty;
            if (hex.Length == 0 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit))
            {
                throw new SignatureInvalidException(SignatureInvalidReason.Malformed, "signedHash is not hex");
            }
            return Convert.FromHexString(hex);
        }

        public virtual string SignWithPfx(PfxSignInput input) =>
            SignWithPfx(input, Convert.FromBase64String(input.PdfContent));

        /// <summary>
        /// PFX podpis nad bajty, které už prošly vstupní kontrolou; <c>input.PdfContent</c> se nečte.
        /// Oba kanály (JSON i binární, #74) končí tady.
        /// </summary>
        public virtual string SignWithPfx(PfxSignInput input, byte[] originalPdf)
        {
            var (chain, privateKey) = PdfCryptoHelper.LoadFromPfx(input.PfxContent, input.PfxPassword);
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
                tsaUrl: input.TsaUrl,
                tsaUsername: input.TsaUsername,
                tsaPassword: input.TsaPassword,
                designWidth: input.DesignWidth,
                designHeight: input.DesignHeight,
                autoHeight: input.AutoHeight,
                signerNameOverride: input.SignerName,
                tsaBackup: TsaEndpoint.From(input.TsaBackupUrl, input.TsaBackupUsername, input.TsaBackupPassword));

            return Convert.ToBase64String(fullySignedPdf);
        }

        public virtual string ApplyDocumentTimestamp(DocumentTimestampInput input) =>
            ApplyDocumentTimestamp(input, Convert.FromBase64String(input.PdfContent));

        /// <summary>
        /// Časové razítko nad bajty, které už prošly vstupní kontrolou (#74, binární kanál);
        /// <c>input.PdfContent</c> se nečte. Stejné chyby ve stejném pořadí jako verze s base64.
        /// </summary>
        public virtual string ApplyDocumentTimestamp(DocumentTimestampInput input, byte[] pdf) =>
            ApplyDocumentTimestamp(input, pdf, keepPdfA: null);

        /// <param name="keepPdfA">Set when the document was prepared as PDF/A (#184): the result must stay so,
        /// otherwise <see cref="DocumentPreparation.NotPreservedCode"/>.</param>
        public virtual string ApplyDocumentTimestamp(DocumentTimestampInput input, byte[] pdf, PdfAConformance? keepPdfA)
        {
            ITSAClient? tsaClient = PdfCryptoHelper.CreateTsaClient(
                input.TsaUrl, input.TsaUsername, input.TsaPassword,
                TsaEndpoint.From(input.TsaBackupUrl, input.TsaBackupUsername, input.TsaBackupPassword));
            if (tsaClient == null)
            {
                throw new ApiValidationException("TSA_NOT_CONFIGURED");
            }

            return ApplyDocumentTimestamp(input, tsaClient, pdf, keepPdfA);
        }

        /// <summary>
        /// The timestamping itself, with the authority handed in. Exists so tests
        /// can drive the real path against an in-process TSA instead of the
        /// network.
        /// </summary>
        internal string ApplyDocumentTimestamp(DocumentTimestampInput input, ITSAClient tsaClient) =>
            ApplyDocumentTimestamp(input, tsaClient, Convert.FromBase64String(input.PdfContent));

        internal string ApplyDocumentTimestamp(DocumentTimestampInput input, ITSAClient tsaClient, byte[] pdf, PdfAConformance? keepPdfA = null)
        {
            var result = DocumentPreparation.KeepPdfA(keepPdfA, () => WriteDocumentTimestamp(input, tsaClient, pdf));
            DocumentPreparation.EnsurePreserved(result, keepPdfA);
            return Convert.ToBase64String(result);
        }

        private static byte[] WriteDocumentTimestamp(DocumentTimestampInput input, ITSAClient tsaClient, byte[] pdf)
        {
            string fieldName = PdfCryptoHelper.EnsureFieldName(input.FieldName, $"Timestamp_{Guid.NewGuid():N}");
            using var msIn = new MemoryStream(pdf);
            using var msOut = new MemoryStream();
            var reader = new PdfReader(msIn);
            var signer = new PdfSigner(reader, msOut, new StampingProperties().UseAppendMode());

            int resolvedPage = input.SignPageNumber <= 0 ? 1 : input.SignPageNumber;
            var signerFrame = PageFrame.Of(signer.GetDocument().GetPage(resolvedPage));

            SignerProperties signerProperties = PdfVisualSigningService.BuildSignerProperties(
                fieldName,
                input.SignRect,
                input.Reason,
                input.Location,
                input.SignPageNumber,
                input.SignImageContent,
                null,
                DocumentPreparation.AppearanceFor(signer.GetDocument(), input.Appearance),
                input.StampImageContent,
                input.BackgroundImageContent,
                input.CompanyLogoContent,
                visible: input.Visible,
                designWidth: input.DesignWidth,
                designHeight: input.DesignHeight,
                autoHeight: input.AutoHeight,
                frame: signerFrame,
                signerNameOverride: input.SignerName);

            signer.SetSignerProperties(signerProperties);
            ApplySignerName(signer, input.SignerName);
            try
            {
                signer.Timestamp(tsaClient, fieldName);
            }
            catch (Exception ex) when (FindTspException(ex) is TspException tsp)
            {
                throw new TsaCommunicationException(
                    input.TsaUrl ?? "(none)",
                    BuildTsaErrorMessage(input.TsaUrl, tsp),
                    ex);
            }

            return msOut.ToArray();
        }

        /// <summary>
        /// Probes a TSA URL by requesting a timestamp for a dummy hash. Throws
        /// <see cref="TsaCommunicationException"/> with a clean message when the
        /// TSA is unreachable or returns a non-RFC-3161 response. Used by the
        /// admin settings save flow.
        /// </summary>
        public void ProbeTsa(string url, string? username, string? password)
        {
            // Free, and takes any URL and password: it must not feed the circuit breaker (#72).
            using var noBreaker = TsaCircuitBreaker.Bypass();
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new TsaCommunicationException("(empty)", "TSA URL is required.");
            }

            ITSAClient? tsaClient;
            try
            {
                tsaClient = PdfCryptoHelper.CreateTsaClient(
                    url,
                    string.IsNullOrWhiteSpace(username) ? null : username,
                    string.IsNullOrWhiteSpace(password) ? null : password);
            }
            catch (Exception ex)
            {
                throw new TsaCommunicationException(url, ex.Message, ex);
            }

            if (tsaClient == null)
            {
                throw new TsaCommunicationException(url, "TSA client could not be initialized.");
            }

            byte[] dummy = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("p4pdf-tsa-probe"));
            try
            {
                var token = tsaClient.GetTimeStampToken(dummy);
                if (token == null || token.Length == 0)
                {
                    throw new TsaCommunicationException(url, "TSA returned an empty timestamp token.");
                }
            }
            catch (TsaCommunicationException) { throw; }
            catch (Exception ex) when (FindTspException(ex) is TspException tsp)
            {
                throw new TsaCommunicationException(url, BuildTsaErrorMessage(url, tsp), ex);
            }
            catch (Exception ex)
            {
                throw new TsaCommunicationException(url,
                    $"TSA at {url} is unreachable or rejected the request: {ex.Message}", ex);
            }
        }

        public virtual string ApplyVisualSign(VisualSignInput input)
        {
            return _visualSigningService.ApplyVisualSign(input);
        }

        /// <summary>Vizuální podpis nad bajty (#74, binární kanál); <c>input.PdfContent</c> se nečte.</summary>
        public virtual string ApplyVisualSign(VisualSignInput input, byte[] pdf)
        {
            return _visualSigningService.ApplyVisualSign(input, pdf);
        }

        /// <param name="keepPdfA">Set when the document was prepared as PDF/A (#184): the result must stay so,
        /// otherwise <see cref="DocumentPreparation.NotPreservedCode"/>.</param>
        public virtual string ApplyVisualSign(VisualSignInput input, byte[] pdf, PdfAConformance? keepPdfA)
        {
            var result = DocumentPreparation.KeepPdfA(keepPdfA, () => _visualSigningService.ApplyVisualSign(input, pdf, keepPdfA));
            DocumentPreparation.EnsurePreserved(Convert.FromBase64String(result), keepPdfA);
            return result;
        }

        public virtual string AddAttachment(AddAttachmentInput input) =>
            AddAttachment(input, null);

        /// <summary>Šifrovat se má nakonfigurovaným certifikátem, ale žádný není (#513).</summary>
        public const string EvidenceCertNotConfiguredCode = "EVIDENCE_CERT_NOT_CONFIGURED";

        /// <summary>
        /// Volající chce nakonfigurovaný certifikát, sám žádný neposlal a server žádný nemá.
        /// Certifikát v požadavku má přednost, takže pak chybějící konfigurace nevadí.
        /// </summary>
        public virtual bool MissingEvidenceCertificate(AddAttachmentInput input) =>
            input.UseConfiguredEncryptionCertificate
            && string.IsNullOrWhiteSpace(input.EncryptForCertificatePem)
            && string.IsNullOrWhiteSpace(_evidenceOptions.EncryptionCertificatePem);

        /// <summary>Je nastavený certifikát pro šifrování evidence (varianta A, #513)?</summary>
        public bool HasConfiguredEvidenceCertificate =>
            !string.IsNullOrWhiteSpace(_evidenceOptions.EncryptionCertificatePem);

        /// <summary>
        /// Příloha nad bajty PDF (#74, binární kanál); <c>input.PdfContent</c> se nečte.
        /// <c>null</c> = PDF je v <c>input.PdfContent</c> (base64).
        /// </summary>
        public virtual string AddAttachment(AddAttachmentInput input, byte[]? pdf)
        {
            if (string.IsNullOrWhiteSpace(input.FileName))
            {
                throw new ArgumentException("A filename is required to attach a file to the PDF.", nameof(input.FileName));
            }

            byte[] pdfBytes = pdf ?? Convert.FromBase64String(input.PdfContent);
            byte[] attachmentBytes = Convert.FromBase64String(input.AttachmentContent);
            string attachmentFileName = input.FileName;
            string? attachmentMimeType = input.MimeType;
            if (MissingEvidenceCertificate(input))
            {
                throw new ApiValidationException(EvidenceCertNotConfiguredCode);
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

            if (pdfDoc.GetConformance().GetAConformance()?.GetPart() == "3")
            {
                // A PDF/A-3 document stays one (#184): the file goes in as an associated file with a MIME type.
                EmbeddedFiles.Add(pdfDoc, uniqueFileName, attachmentBytes, input.Description, uniqueFileName, attachmentMimeType, PdfName.Unspecified);
                pdfDoc.Close();
                return Convert.ToBase64String(msOut.ToArray());
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
            string? signerNameOverride = null,
            bool disableTsa = false,
            TsaEndpoint? tsaBackup = null)
        {
            var preSignContainer = new DigestCalcBlankSigner(PdfName.Adobe_PPKLite, PdfCryptoHelper.SignatureSubFilter);
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
            // A timestamp is applied only when the caller asks for one. This server
            // has no TSA of its own to fall back on — stamping with an authority the
            // caller never chose isn't a decision that's ours to make.
            ITSAClient? tsaClient = disableTsa
                ? null
                : PdfCryptoHelper.CreateTsaClient(tsaUrl, tsaUsername, tsaPassword, tsaBackup);
            var tsaUrlForError = tsaUrl;
            return InjectFinalSignature(pdfWithPlaceholder, signatureBytes, chain, fieldName, tsaClient, tsaUrlForErrorContext: tsaUrlForError);
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
            string? signerNameOverride = null,
            ContentStampPlan? stamp = null)
        {
            using var msIn = new MemoryStream(originalPdf);
            using var msOut = new MemoryStream();

            var reader = new PdfReader(msIn);
            var signer = new PdfSigner(reader, msOut, new StampingProperties().UseAppendMode());

            int resolvedPage = pageNumber <= 0 ? 1 : pageNumber;
            var signerFrame = PageFrame.Of(signer.GetDocument().GetPage(resolvedPage));

            // The stamp goes into the signature's own revision, before the field, so the signature covers it (portál #511).
            if (stamp != null)
            {
                if (stamp.AsAnnotation) PdfVisualSigningService.AddAsAnnotation(stamp.Stamp, signer.GetDocument());
                else PdfVisualSigningService.DrawIntoContent(stamp.Stamp, signer.GetDocument());
            }

            // PDF/A wants every font embedded; without a family iText falls back to plain Helvetica and fails.
            appearance = DocumentPreparation.AppearanceFor(signer.GetDocument(), appearance);

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
                frame: signerFrame,
                signerNameOverride: signerNameOverride);

            signer.SetSignerProperties(signerProperties);
            ApplySignerName(signer, signerNameOverride, stamp == null ? null : SealPlacement.StampMarker(stamp.Stamp.Page, stamp.Stamp.PageRect));
            // Reserve extra space so the deferred signature can include TSA timestamp tokens when present.
            signer.SignExternalContainer(container, 32768);

            return msOut.ToArray();
        }

        private static byte[] InjectFinalSignature(byte[] pdfWithPlaceholder, byte[] signatureBytes, IX509Certificate[] chain, string fieldName, ITSAClient? tsaClient, string? tsaUrlForErrorContext = null)
        {
            using var msIn = new MemoryStream(pdfWithPlaceholder);
            using var msOut = new MemoryStream();

            var reader = new PdfReader(msIn);
            IExternalSignatureContainer external = new ExternalSignatureContainer(chain, signatureBytes, tsaClient);

            try
            {
                PdfSigner.SignDeferred(reader, fieldName, msOut, external);
            }
            catch (Exception ex) when (ex is not SignatureInvalidException && FindInner<SignatureInvalidException>(ex) is { } invalid)
            {
                // However iText wraps it, a refused client signature stays a refused signature.
                throw invalid;
            }
            catch (Exception ex) when (FindTspException(ex) is TspException tsp)
            {
                throw new TsaCommunicationException(
                    tsaUrlForErrorContext ?? "(none)",
                    BuildTsaErrorMessage(tsaUrlForErrorContext, tsp),
                    ex);
            }

            return msOut.ToArray();
        }

        /// <summary>
        /// Puts the signer into the signature dictionary so a reader's
        /// "Signed by" agrees with what the appearance shows.
        ///
        /// Empty is left alone rather than written as an empty string: an
        /// absent /Name lets the reader fall back to the certificate, which is
        /// at least true, whereas a blank one says the document was signed by
        /// nobody.
        /// </summary>
        private static void ApplySignerName(PdfSigner signer, string? signerName, PdfArray? stampMarker = null)
        {
            if (string.IsNullOrWhiteSpace(signerName) && stampMarker == null) return;
            signer.SetSignatureEvent(new SignerNameEvent(signerName, stampMarker));
        }

        private static T? FindInner<T>(Exception ex) where T : Exception
        {
            for (var current = ex.InnerException; current != null; current = current.InnerException)
            {
                if (current is T match) return match;
            }
            return null;
        }

        private static TspException? FindTspException(Exception ex)
        {
            for (var current = (Exception?)ex; current != null; current = current.InnerException)
            {
                if (current is TspException tsp) return tsp;
            }
            return null;
        }

        private static string BuildTsaErrorMessage(string? tsaUrl, TspException tsp)
        {
            var urlPart = string.IsNullOrWhiteSpace(tsaUrl) ? "" : $" ({tsaUrl})";
            var detail = tsp.Message ?? "unknown failure";
            return $"Timestamp authority{urlPart} did not return a valid response: {detail}. " +
                   "Check that the TSA URL is reachable, supports RFC 3161, and that any required credentials are correct.";
        }
    }
}
