namespace DotNetSigningServer.Models
{
    public class SignatureAppearanceOptions
    {
        public string? DescriptionText { get; set; }
        public string? ForegroundColor { get; set; }
        public string? BackgroundColor { get; set; }
        public string? FontFamily { get; set; }
        public float? FontSize { get; set; }
        public bool ShowReason { get; set; } = true;
        public bool ShowLocation { get; set; } = true;
        public bool ShowDate { get; set; } = true;
        public bool ShowSignerName { get; set; } = true;
        public bool ShowCompanyName { get; set; } = true;
        public bool BackgroundRepeat { get; set; } = true;
        public bool? AutoFontSize { get; set; }
        public SignatureLabels? Labels { get; set; }
    }

    public class SignatureLabels
    {
        public string? Reason { get; set; }
        public string? Location { get; set; }
        public string? Date { get; set; }
        public string? Signer { get; set; }
        public string? Company { get; set; }
    }
}
