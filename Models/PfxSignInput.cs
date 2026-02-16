namespace DotNetSigningServer.Models
{
    public class PfxSignInput
    {
        public string PdfContent { get; set; } = "";
        public string PfxContent { get; set; } = "";
        public string PfxPassword { get; set; } = "";
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
        public Guid? TemplateId { get; set; }
    }
}
