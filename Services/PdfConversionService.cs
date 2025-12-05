using DotNetSigningServer.Models;
using iText.Forms;
using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using iText.Pdfa;
using Microsoft.Extensions.Logging;

namespace DotNetSigningServer.Services
{
    public class PdfConversionService
    {
        private readonly ILogger<PdfConversionService> _logger;
        private readonly string _iccProfilePath;

        public PdfConversionService(ILogger<PdfConversionService> logger)
        {
            _logger = logger;
            _iccProfilePath = Path.Combine(AppContext.BaseDirectory, "Resources", "srgb.icc");
        }

        public string ConvertToPdfA(ConvertToPdfAInput input)
        {
            byte[] originalPdf;
            try
            {
                originalPdf = Convert.FromBase64String(input.PdfContent);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("PdfContent is not valid base64.", ex);
            }

            if (!File.Exists(_iccProfilePath))
            {
                throw new FileNotFoundException("ICC profile required for PDF/A conversion was not found.", _iccProfilePath);
            }

            var conformance = ResolveConformance(input.Conformance);

            using var outputStream = new MemoryStream();
            using var inputStream = new MemoryStream(originalPdf);
            using var iccStream = File.OpenRead(_iccProfilePath);

            try
            {
                var outputIntent = new PdfOutputIntent("Custom", "", null, "sRGB IEC61966-2.1", iccStream);
                var writerProperties = new WriterProperties().AddXmpMetadata();

                using var writer = new PdfWriter(outputStream, writerProperties);
                using var pdfaDoc = new PdfADocument(writer, conformance, outputIntent);
                using var sourceDoc = new PdfDocument(new PdfReader(inputStream));

                pdfaDoc.SetTagged();
                pdfaDoc.GetCatalog().SetLang(new PdfString("en-US"));

                var copier = new PdfPageFormCopier();
                sourceDoc.CopyPagesTo(1, sourceDoc.GetNumberOfPages(), pdfaDoc, copier);

                pdfaDoc.Close();
                return Convert.ToBase64String(outputStream.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to convert PDF to PDF/A");
                throw;
            }
        }

        public static string FormatConformance(string? value)
        {
            var normalized = (value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return "PDF/A-2B";
            }

            return normalized.ToUpperInvariant() switch
            {
                "PDF/A-1A" or "PDFA-1A" or "PDF_A_1A" => "PDF/A-1A",
                "PDF/A-1B" or "PDFA-1B" or "PDF_A_1B" => "PDF/A-1B",
                "PDF/A-2A" or "PDFA-2A" or "PDF_A_2A" => "PDF/A-2A",
                "PDF/A-2B" or "PDFA-2B" or "PDF_A_2B" => "PDF/A-2B",
                "PDF/A-2U" or "PDFA-2U" or "PDF_A_2U" => "PDF/A-2U",
                "PDF/A-3A" or "PDFA-3A" or "PDF_A_3A" => "PDF/A-3A",
                "PDF/A-3B" or "PDFA-3B" or "PDF_A_3B" => "PDF/A-3B",
                "PDF/A-3U" or "PDFA-3U" or "PDF_A_3U" => "PDF/A-3U",
                "PDF/A-4" or "PDFA-4" or "PDF_A_4" => "PDF/A-4",
                "PDF/A-4E" or "PDFA-4E" or "PDF_A_4E" => "PDF/A-4E",
                "PDF/A-4F" or "PDFA-4F" or "PDF_A_4F" => "PDF/A-4F",
                _ => "PDF/A-2B"
            };
        }

        private static PdfAConformance ResolveConformance(string? value)
        {
            return FormatConformance(value) switch
            {
                "PDF/A-1A" => PdfAConformance.PDF_A_1A,
                "PDF/A-1B" => PdfAConformance.PDF_A_1B,
                "PDF/A-2A" => PdfAConformance.PDF_A_2A,
                "PDF/A-2B" => PdfAConformance.PDF_A_2B,
                "PDF/A-2U" => PdfAConformance.PDF_A_2U,
                "PDF/A-3A" => PdfAConformance.PDF_A_3A,
                "PDF/A-3B" => PdfAConformance.PDF_A_3B,
                "PDF/A-3U" => PdfAConformance.PDF_A_3U,
                "PDF/A-4" => PdfAConformance.PDF_A_4,
                "PDF/A-4E" => PdfAConformance.PDF_A_4E,
                "PDF/A-4F" => PdfAConformance.PDF_A_4F,
                _ => PdfAConformance.PDF_A_2B
            };
        }
    }
}
