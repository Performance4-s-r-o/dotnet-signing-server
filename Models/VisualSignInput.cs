namespace DotNetSigningServer.Models
{
    public class VisualSignInput : IDocumentPreparationRequest
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
        /// <summary>Signature design width in PDF points. When set, layout uses this instead of SignRect.Width.</summary>
        public float? DesignWidth { get; set; }
        /// <summary>Signature design height in PDF points. When set, layout uses this instead of SignRect.Height.</summary>
        public float? DesignHeight { get; set; }
        /// <summary>When true, signature box height grows to fit content regardless of DesignHeight.</summary>
        public bool? AutoHeight { get; set; }
        /// <summary>Flatten the form and remove active content first (#184); skipped on a signed document.</summary>
        public bool FlattenForm { get; set; }
        /// <summary>Convert to PDF/A before signing (#184). A signed document must already be PDF/A.</summary>
        public bool ConvertToPdfa { get; set; }
        /// <summary>"PDF/A-2B" (default) or "PDF/A-3B"; anything else is refused.</summary>
        public string? PdfaConformance { get; set; }
    }
}
