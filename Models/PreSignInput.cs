namespace DotNetSigningServer.Models
{
    public class PreSignInput : IDocumentPreparationRequest
    {
        public string CertificatePem { get; set; } = "";
        public string PdfContent { get; set; } = "";
        public string Location { get; set; } = "";
        public string Reason { get; set; } = "";
        /// <summary>PDF points, origin bottom left at the CropBox corner of the page as displayed (after <c>/Rotate</c>).</summary>
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
        /// <summary>Tried when TsaUrl fails (network, HTTP or a rejected response).</summary>
        public string? TsaBackupUrl { get; set; }
        public string? TsaBackupUsername { get; set; }
        public string? TsaBackupPassword { get; set; }
        public Guid? TemplateId { get; set; }
        /// <summary>Verification URL to embed in PDF (e.g. "https://verify.performance4pdf.com/abc123").</summary>
        public string? VerificationUrl { get; set; }
        /// <summary>"disabled" | "link" (PDF metadata) | "qr" (append QR page).</summary>
        public string? VerificationMode { get; set; }
        /// <summary>Signature design width in PDF points. When set, layout uses this instead of SignRect.Width.</summary>
        public float? DesignWidth { get; set; }
        /// <summary>Signature design height in PDF points. When set, layout uses this instead of SignRect.Height.</summary>
        public float? DesignHeight { get; set; }
        /// <summary>When true, signature box height grows to fit content regardless of DesignHeight.</summary>
        public bool? AutoHeight { get; set; }
        /// <summary>Display name used in the signer row. Overrides CN/GN+SN extracted from the cert chain when provided.</summary>
        public string? SignerName { get; set; }
        /// <summary>Custom fillable text fields stamped into the PDF before the hash is computed, so they fall under this signature.</summary>
        public List<PreSignFieldInput>? Fields { get; set; }
        /// <summary>Image signature drawn into the page content before the field, covered by this signature (portál #511).</summary>
        public ContentStampInput? ContentStamp { get; set; }
        /// <summary>
        /// The field goes beside <see cref="ContentStamp"/> on its page: <c>SignRect</c> gives only its size,
        /// the position and <c>SignPageNumber</c> are computed. Requires <see cref="ContentStamp"/>.
        /// </summary>
        public bool PlaceBesideContentStamp { get; set; }
        /// <summary>Boxes of other signers the stamp and seal must not cover, in PDF points of the client frame like <c>SignRect</c>.</summary>
        public List<OccupiedRect>? Occupied { get; set; }
        /// <summary>Flatten the form and remove active content before anything else (#184); skipped on a signed document.</summary>
        public bool FlattenForm { get; set; }
        /// <summary>Convert to PDF/A before the signature is prepared (#184). A signed document must already be PDF/A.</summary>
        public bool ConvertToPdfa { get; set; }
        /// <summary>"PDF/A-2B" (default) or "PDF/A-3B"; anything else is refused.</summary>
        public string? PdfaConformance { get; set; }
    }

    public class SignRect
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }
}
