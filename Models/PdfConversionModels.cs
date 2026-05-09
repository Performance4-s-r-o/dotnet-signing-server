namespace DotNetSigningServer.Models
{
    public class ConvertToPdfAInput
    {
        public static readonly IReadOnlyList<string> SupportedConformanceLevels = new[]
        {
            "PDF/A-1A", "PDF/A-1B",
            "PDF/A-2A", "PDF/A-2B", "PDF/A-2U",
            "PDF/A-3A", "PDF/A-3B", "PDF/A-3U",
            "PDF/A-4", "PDF/A-4E", "PDF/A-4F"
        };

        public string PdfContent { get; set; } = string.Empty; // Base64
        public string? Conformance { get; set; } = "PDF/A-2B";
    }
}
