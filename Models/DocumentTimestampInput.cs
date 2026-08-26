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
        /// <summary>Display name used in the signer row. Falls back to empty when unset.</summary>
        public string? SignerName { get; set; }
        /// <summary>
        /// Whether the timestamp draws a box on the page. Defaults to true for the
        /// standalone "stamp this document" use.
        ///
        /// Archive timestamps must pass false: a B-LTA document is re-timestamped
        /// every few years, and a visible box per renewal would progressively cover
        /// the page with marks that carry no meaning for the reader. An invisible
        /// timestamp is still listed in the viewer's signature panel.
        /// </summary>
        public bool Visible { get; set; } = true;
    }
}
