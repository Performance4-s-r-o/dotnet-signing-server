namespace DotNetSigningServer.Models
{
    public class PreSignInput
    {
        public string CertificatePem { get; set; } = "";
        public string PdfContent { get; set; } = "";
        public string Location { get; set; } = "";
        public string Reason { get; set; } = "";
        public SignRect SignRect { get; set; } = new();
        public string? SignImageContent { get; set; }
        public string? StampImageContent { get; set; }
        public string? CompanyLogoContent { get; set; }
        public string? BackgroundImageContent { get; set; }
        public int SignPageNumber { get; set; } = 1;
        public string? FieldName { get; set; }
        public SignatureAppearanceOptions? Appearance { get; set; }
        public string? TsaUrl { get; set; }
        public string? TsaUsername { get; set; }
        public string? TsaPassword { get; set; }
        public Guid? TemplateId { get; set; }
        /// <summary>Verification URL to embed in PDF (e.g. "https://verify.p4pdf.com/abc123").</summary>
        public string? VerificationUrl { get; set; }
        /// <summary>"disabled" | "link" (PDF metadata) | "qr" (append QR page).</summary>
        public string? VerificationMode { get; set; }
    }

    public class SignRect
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }
}
