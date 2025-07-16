namespace DotNetSigningServer.Models
{
    public class PreSignInput
    {
        public string CertificatePem { get; set; } = "";
        public string PdfContent { get; set; } = "";
        public string Location { get; set; } = "";
        public string Reason { get; set; } = "";
        public SignRect SignRect { get; set; } = new();
        public string? SignImageContent { get; set; }
        public int SignPageNumber { get; set; } = 1;
    }

    public class SignRect
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }
}
