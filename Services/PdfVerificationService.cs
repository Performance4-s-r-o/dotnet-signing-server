using iText.Kernel.Pdf;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.Barcodes;
using iText.IO.Font.Constants;

namespace DotNetSigningServer.Services
{
    /// <summary>
    /// Adds verification metadata or QR code pages to PDFs before signing.
    /// Must be called before the signing step so the signature covers the added content.
    /// </summary>
    public static class PdfVerificationService
    {
        /// <summary>
        /// Add verification info to a PDF based on the mode.
        /// Returns the modified PDF bytes.
        /// </summary>
        public static byte[] AddVerification(byte[] pdfBytes, string verificationUrl, string mode, string? signerName = null)
        {
            if (string.IsNullOrWhiteSpace(verificationUrl) || mode == "disabled")
                return pdfBytes;

            if (mode == "link")
            {
                using var inputStream = new MemoryStream(pdfBytes);
                using var outputStream = new MemoryStream();
                var reader = new PdfReader(inputStream);
                var writer = new PdfWriter(outputStream);
                var pdfDoc = new PdfDocument(reader, writer);
                AddMetadataLink(pdfDoc, verificationUrl);
                pdfDoc.Close();
                return outputStream.ToArray();
            }

            if (mode == "qr")
            {
                // Create QR page as a separate PDF, then merge into the original.
                // This avoids iText layout issues with Canvas writing to page 1.
                byte[] qrPagePdf = CreateQrPagePdf(verificationUrl, signerName);
                return MergePdfs(pdfBytes, qrPagePdf);
            }

            return pdfBytes;
        }

        /// <summary>
        /// Add verification URL to PDF custom properties (Document Properties > Custom).
        /// Not visible in the document but machine-readable.
        /// </summary>
        private static void AddMetadataLink(PdfDocument pdfDoc, string verificationUrl)
        {
            var info = pdfDoc.GetDocumentInfo();
            info.SetMoreInfo("P4PDF-Verification-URL", verificationUrl);
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

            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            var fontBold = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);

            doc.Add(new Paragraph("Document Verification")
                .SetFont(fontBold)
                .SetFontSize(18)
                .SetTextAlignment(TextAlignment.CENTER)
                .SetMarginTop(60));

            doc.Add(new Paragraph("This document was signed using P4PDF. " +
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
