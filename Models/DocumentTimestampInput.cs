namespace DotNetSigningServer.Models
{
    public class DocumentTimestampInput
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
        public string? FieldName { get; set; }
        public SignatureAppearanceOptions? Appearance { get; set; }
        public string? TsaUrl { get; set; }
        public string? TsaUsername { get; set; }
        public string? TsaPassword { get; set; }
        public Guid? TemplateId { get; set; }
        /// <summary>Signature design width in PDF points. When set, layout uses this instead of SignRect.Width.</summary>
        public float? DesignWidth { get; set; }
        /// <summary>Signature design height in PDF points. When set, layout uses this instead of SignRect.Height.</summary>
        public float? DesignHeight { get; set; }
        /// <summary>When true, signature box height grows to fit content regardless of DesignHeight.</summary>
        public bool? AutoHeight { get; set; }
    }
}
