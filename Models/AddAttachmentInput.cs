namespace DotNetSigningServer.Models
{
    public class AddAttachmentInput
    {
        public string PdfContent { get; set; } = "";
        public string AttachmentContent { get; set; } = "";
        public string FileName { get; set; } = "";
        public string? Description { get; set; }
        public string? MimeType { get; set; }
    }
}
