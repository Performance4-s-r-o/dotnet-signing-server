namespace DotNetSigningServer.Models
{
    /// <summary>
    /// Image signature drawn into the page content in presign, before the signature field (portál #511).
    /// The same renderer and fields as <c>/api/visual-sign</c>, so it looks like a certificate signature.
    /// </summary>
    public class ContentStampInput
    {
        public string Location { get; set; } = "";
        public string Reason { get; set; } = "";
        public SignRect SignRect { get; set; } = new();
        public int SignPageNumber { get; set; } = 1;
        public string? SignImageContent { get; set; }
        public string? StampImageContent { get; set; }
        public string? CompanyLogoContent { get; set; }
        public string? BackgroundImageContent { get; set; }
        public SignatureAppearanceOptions? Appearance { get; set; }
        public string? SignerName { get; set; }
        /// <summary>Company row of the stamp; empty = none (there is no certificate to read it from).</summary>
        public string? CompanyName { get; set; }
        public float? DesignWidth { get; set; }
        public float? DesignHeight { get; set; }
        public bool? AutoHeight { get; set; }

        internal VisualSignInput ToVisualSign() => new()
        {
            Location = Location,
            Reason = Reason,
            SignRect = SignRect,
            SignPageNumber = SignPageNumber,
            SignImageContent = SignImageContent,
            StampImageContent = StampImageContent,
            CompanyLogoContent = CompanyLogoContent,
            BackgroundImageContent = BackgroundImageContent,
            Appearance = Appearance,
            SignerName = SignerName,
            DesignWidth = DesignWidth,
            DesignHeight = DesignHeight,
            AutoHeight = AutoHeight,
        };
    }

    /// <summary>Where presign drew the content stamp and put the signature field (portál #511).</summary>
    public record ContentStampPlacement(int Page, SignRect Stamp, SignRect Field);
}
