using iText.Kernel.Pdf;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.Barcodes;
using iText.Signatures;

namespace DotNetSigningServer.Services
{
    /// <summary>
    /// Adds verification metadata or QR code pages to PDFs before signing.
    /// Must be called before the signing step so the signature covers the added content.
    /// </summary>
    public static class PdfVerificationService
    {
        private const string MetadataKey = "P4PDF-Verification-URL";

        /// <summary>
        /// Add verification info to a PDF based on the mode.
        /// Returns the modified PDF bytes.
        /// </summary>
        public static byte[] AddVerification(byte[] pdfBytes, string verificationUrl, string mode, string? signerName = null)
        {
            if (string.IsNullOrWhiteSpace(verificationUrl) || mode == "disabled")
                return pdfBytes;

            // This runs before the signature that is about to be added, but on a
            // document that may already carry signatures from earlier signers.
            // Rewriting such a file in full re-serializes every existing
            // /Contents and destroys it -- Adobe then reports
            // "SigDict /Contents illegal data" for all of them and only the last
            // signature survives. Anything done here from now on must be an
            // incremental update.
            bool alreadySigned = HasSignatures(pdfBytes);

            if (mode == "qr" && !alreadySigned)
            {
                // Create QR page as a separate PDF, then merge into the original.
                // This avoids iText layout issues with Canvas writing to page 1.
                byte[] qrPagePdf = CreateQrPagePdf(verificationUrl, signerName);
                return MergePdfs(pdfBytes, qrPagePdf);
            }

            // "link", plus "qr" once the document is signed: a second QR page
            // would make Adobe flag every earlier signature as "a page was
            // changed", and a multi-signer document would end up with one QR
            // page per signer. Later signers get the invisible metadata entry
            // instead, which append mode can add without touching the signatures.
            if (mode == "link" || mode == "qr")
            {
                using var inputStream = new MemoryStream(pdfBytes);
                using var outputStream = new MemoryStream();
                var reader = new PdfReader(inputStream);
                var writer = new PdfWriter(outputStream);
                var pdfDoc = new PdfDocument(reader, writer, new StampingProperties().UseAppendMode());
                AddMetadataLink(pdfDoc, verificationUrl);
                pdfDoc.Close();
                return outputStream.ToArray();
            }

            return pdfBytes;
        }

        /// <summary>
        /// Whether the document already carries signatures, i.e. whether it is
        /// still safe to rewrite it in full.
        /// </summary>
        private static bool HasSignatures(byte[] pdfBytes)
        {
            try
            {
                using var stream = new MemoryStream(pdfBytes);
                using var reader = new PdfReader(stream);
                using var document = new PdfDocument(reader);
                return new SignatureUtil(document).GetSignatureNames().Count > 0;
            }
            catch
            {
                // A document we cannot even open will fail loudly in the signing
                // step right after this one. Until then, assume it is signed so
                // that nothing gets rewritten on a guess.
                return true;
            }
        }

        /// <summary>
        /// Add verification URL to PDF custom properties (Document Properties > Custom).
        /// Not visible in the document but machine-readable.
        /// </summary>
        private static void AddMetadataLink(PdfDocument pdfDoc, string verificationUrl)
        {
            var info = pdfDoc.GetDocumentInfo();

            // Each signer brings its own verification URL, so reusing the key
            // would silently drop the previous signer's. Same suffix scheme as
            // the evidence attachments in PdfSigningService.
            string key = MetadataKey;
            int counter = 2;
            while (info.GetMoreInfo(key) != null)
            {
                key = $"{MetadataKey}-{counter}";
                counter++;
            }

            info.SetMoreInfo(key, verificationUrl);
        }

        /// <summary>
        /// Create a standalone single-page PDF with QR code and verification details.
        /// </summary>
        private static byte[] CreateQrPagePdf(string verificationUrl, string? signerName)
        {
            using var ms = new MemoryStream();
            var writer = new PdfWriter(ms);
            var pdfDoc = new PdfDocument(writer);
            var doc = new Document(pdfDoc, PageSize.A4);

            var font = AppFonts.Load(AppFontFamily.Sans);
            var fontBold = AppFonts.Load(AppFontFamily.Sans, bold: true);

            doc.Add(new Paragraph("Document Verification")
                .SetFont(fontBold)
                .SetFontSize(18)
                .SetTextAlignment(TextAlignment.CENTER)
                .SetMarginTop(60));

            doc.Add(new Paragraph("This document was signed using Performance4PDF. " +
                "Scan the QR code below or visit the verification URL to verify the document's integrity.")
                .SetFont(font)
                .SetFontSize(10)
                .SetTextAlignment(TextAlignment.CENTER)
                .SetMarginTop(10)
                .SetFontColor(iText.Kernel.Colors.ColorConstants.DARK_GRAY));

            var qrCode = new BarcodeQRCode(verificationUrl);
            var qrImage = new Image(qrCode.CreateFormXObject(pdfDoc))
                .SetWidth(150)
                .SetHeight(150)
                .SetHorizontalAlignment(HorizontalAlignment.CENTER)
                .SetMarginTop(30);
            doc.Add(qrImage);

            doc.Add(new Paragraph(verificationUrl)
                .SetFont(font)
                .SetFontSize(9)
                .SetTextAlignment(TextAlignment.CENTER)
                .SetMarginTop(10)
                .SetFontColor(iText.Kernel.Colors.ColorConstants.DARK_GRAY));

            if (!string.IsNullOrWhiteSpace(signerName))
            {
                doc.Add(new Paragraph($"Signed by: {signerName}")
                    .SetFont(font)
                    .SetFontSize(10)
                    .SetTextAlignment(TextAlignment.CENTER)
                    .SetMarginTop(20));
            }

            doc.Add(new Paragraph($"Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC")
                .SetFont(font)
                .SetFontSize(10)
                .SetTextAlignment(TextAlignment.CENTER)
                .SetMarginTop(5));

            doc.Close();
            return ms.ToArray();
        }

        /// <summary>
        /// Merge two PDFs — append all pages from the second PDF to the first.
        /// Rewrites the file in full, so the caller must only reach this with an
        /// unsigned document (see <see cref="HasSignatures"/>).
        /// </summary>
        private static byte[] MergePdfs(byte[] mainPdf, byte[] appendPdf)
        {
            using var mainStream = new MemoryStream(mainPdf);
            using var appendStream = new MemoryStream(appendPdf);
            using var outputStream = new MemoryStream();

            var mainReader = new PdfReader(mainStream);
            var appendReader = new PdfReader(appendStream);
            var writer = new PdfWriter(outputStream);

            var mainDoc = new PdfDocument(mainReader, writer);
            var appendDoc = new PdfDocument(appendReader);

            appendDoc.CopyPagesTo(1, appendDoc.GetNumberOfPages(), mainDoc);

            appendDoc.Close();
            mainDoc.Close();

            return outputStream.ToArray();
        }
    }
}
