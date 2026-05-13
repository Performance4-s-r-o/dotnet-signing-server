namespace DotNetSigningServer.Models
{
    public class SealInput
    {
        public string PdfContent { get; set; } = "";
        public string Location { get; set; } = "";
        public string Reason { get; set; } = "";
        public SignRect SignRect { get; set; } = new();
        public string? SignImageContent { get; set; }
        public string? StampImageContent { get; set; }
        public string? CompanyLogoContent { get; set; }
        public string? BackgroundImageContent { get; set; }
        public int SignPageNumber { get; set; } = 1;
        public SignatureAppearanceOptions? Appearance { get; set; }
        public Guid? TemplateId { get; set; }
        public string? SignerName { get; set; }
        public string? TsaUrl { get; set; }
        public string? TsaUsername { get; set; }
        public string? TsaPassword { get; set; }
        /// <summary>When true, do not apply any timestamp — overrides env-configured TSA fallback.</summary>
        public bool DisableTsa { get; set; }
        /// <summary>Verification URL to embed in PDF (e.g. "https://verify.p4pdf.com/abc123").</summary>
        public string? VerificationUrl { get; set; }
        /// <summary>"disabled" | "link" (PDF metadata) | "qr" (append QR page).</summary>
        public string? VerificationMode { get; set; }
        /// <summary>Signature design width in PDF points. When set, layout uses this instead of SignRect.Width.</summary>
        public float? DesignWidth { get; set; }
        /// <summary>Signature design height in PDF points. When set, layout uses this instead of SignRect.Height.</summary>
        public float? DesignHeight { get; set; }
        /// <summary>When true, signature box height grows to fit content regardless of DesignHeight.</summary>
        public bool? AutoHeight { get; set; }
    }
}
