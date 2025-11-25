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
        public int SignPageNumber { get; set; } = 1;
        public string? FieldName { get; set; }
    }
}
