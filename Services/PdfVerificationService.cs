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

            using var inputStream = new MemoryStream(pdfBytes);
            using var outputStream = new MemoryStream();

            var reader = new PdfReader(inputStream);
            var writer = new PdfWriter(outputStream);
            var pdfDoc = new PdfDocument(reader, writer);

            switch (mode)
            {
                case "link":
                    AddMetadataLink(pdfDoc, verificationUrl);
                    break;
                case "qr":
                    AddQrPage(pdfDoc, verificationUrl, signerName);
                    break;
            }

            pdfDoc.Close();
            return outputStream.ToArray();
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
        /// Append a new page with a QR code and verification details.
        /// </summary>
        private static void AddQrPage(PdfDocument pdfDoc, string verificationUrl, string? signerName)
        {
            var pageSize = PageSize.A4;
            pdfDoc.AddNewPage(pageSize);
            var lastPageNum = pdfDoc.GetNumberOfPages();
            var page = pdfDoc.GetPage(lastPageNum);

            using var doc = new Document(pdfDoc, pageSize, false);
            doc.SetRenderer(new iText.Layout.Renderer.DocumentRenderer(doc));

            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            var fontBold = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);

            // Title
            var title = new Paragraph("Document Verification")
                .SetFont(fontBold)
                .SetFontSize(18)
                .SetTextAlignment(TextAlignment.CENTER)
                .SetMarginTop(60);
            doc.Add(title);

            // Description
            var description = new Paragraph("This document was signed using P4PDF. " +
                "Scan the QR code below or visit the verification URL to verify the document's integrity.")
                .SetFont(font)
                .SetFontSize(10)
                .SetTextAlignment(TextAlignment.CENTER)
                .SetMarginTop(10)
                .SetFontColor(iText.Kernel.Colors.ColorConstants.DARK_GRAY);
            doc.Add(description);

            // QR Code
            var qrCode = new BarcodeQRCode(verificationUrl);
            var qrImage = new Image(qrCode.CreateFormXObject(pdfDoc))
                .SetWidth(150)
                .SetHeight(150)
                .SetHorizontalAlignment(HorizontalAlignment.CENTER)
                .SetMarginTop(30);
            doc.Add(qrImage);

            // Verification URL
            var urlParagraph = new Paragraph(verificationUrl)
                .SetFont(font)
                .SetFontSize(9)
                .SetTextAlignment(TextAlignment.CENTER)
                .SetMarginTop(10)
                .SetFontColor(iText.Kernel.Colors.ColorConstants.DARK_GRAY);
            doc.Add(urlParagraph);

            // Signer info
            if (!string.IsNullOrWhiteSpace(signerName))
            {
                var signerParagraph = new Paragraph($"Signed by: {signerName}")
                    .SetFont(font)
                    .SetFontSize(10)
                    .SetTextAlignment(TextAlignment.CENTER)
                    .SetMarginTop(20);
                doc.Add(signerParagraph);
            }

            // Date
            var dateParagraph = new Paragraph($"Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC")
                .SetFont(font)
                .SetFontSize(10)
                .SetTextAlignment(TextAlignment.CENTER)
                .SetMarginTop(5);
            doc.Add(dateParagraph);

            doc.Flush();
        }
    }
}
