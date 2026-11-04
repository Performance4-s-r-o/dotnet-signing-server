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

    /// <summary>
    /// Where presign drew the content stamp and put the signature field (portál #511).
    /// <c>Annotation</c>: the stamp is a locked <c>/Stamp</c> annotation (the document was already signed),
    /// otherwise page content. <c>Version</c> 2 = earlier stamps, <c>occupied</c> and the CropBox are honoured;
    /// 3 = rects in the client frame (CropBox origin and <c>/Rotate</c>), unsigned signature fields are no obstacle.
    /// </summary>
    public record ContentStampPlacement(int Page, SignRect Stamp, SignRect Field, bool Annotation = false, int Version = ContentStampPlacement.CurrentVersion)
    {
        public const int CurrentVersion = 3;
    }

    /// <summary>A box the stamp and seal must not cover, reserved by the caller for another signer (portál #511), client frame.</summary>
    public class OccupiedRect
    {
        public int Page { get; set; } = 1;
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }
}
