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
            var newPage = pdfDoc.AddNewPage(pageSize);
            var lastPageNum = pdfDoc.GetNumberOfPages();

            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            var fontBold = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);

            // Use PdfCanvas to write directly on the new page (avoids Document
            // writing to page 1 when the PDF already has content).
            var canvas = new iText.Kernel.Pdf.Canvas.PdfCanvas(newPage);
            var rootArea = new Rectangle(
                pageSize.GetLeft() + 50, pageSize.GetBottom() + 50,
                pageSize.GetWidth() - 100, pageSize.GetHeight() - 100);

            using var layoutCanvas = new iText.Layout.Canvas(canvas, rootArea);

            // Title
            layoutCanvas.Add(new Paragraph("Document Verification")
                .SetFont(fontBold)
                .SetFontSize(18)
                .SetTextAlignment(TextAlignment.CENTER)
                .SetMarginTop(40));

            // Description
            layoutCanvas.Add(new Paragraph("This document was signed using P4PDF. " +
                "Scan the QR code below or visit the verification URL to verify the document's integrity.")
                .SetFont(font)
                .SetFontSize(10)
                .SetTextAlignment(TextAlignment.CENTER)
                .SetMarginTop(10)
                .SetFontColor(iText.Kernel.Colors.ColorConstants.DARK_GRAY));

            // QR Code
            var qrCode = new BarcodeQRCode(verificationUrl);
            var qrImage = new Image(qrCode.CreateFormXObject(pdfDoc))
                .SetWidth(150)
                .SetHeight(150)
                .SetHorizontalAlignment(HorizontalAlignment.CENTER)
                .SetMarginTop(30);
            layoutCanvas.Add(qrImage);

            // Verification URL
            layoutCanvas.Add(new Paragraph(verificationUrl)
                .SetFont(font)
                .SetFontSize(9)
                .SetTextAlignment(TextAlignment.CENTER)
                .SetMarginTop(10)
                .SetFontColor(iText.Kernel.Colors.ColorConstants.DARK_GRAY));

            // Signer info
            if (!string.IsNullOrWhiteSpace(signerName))
            {
                layoutCanvas.Add(new Paragraph($"Signed by: {signerName}")
                    .SetFont(font)
                    .SetFontSize(10)
                    .SetTextAlignment(TextAlignment.CENTER)
                    .SetMarginTop(20));
            }

            // Date
            layoutCanvas.Add(new Paragraph($"Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC")
                .SetFont(font)
                .SetFontSize(10)
                .SetTextAlignment(TextAlignment.CENTER)
                .SetMarginTop(5));
        }
    }
}
