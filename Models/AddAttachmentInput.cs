namespace DotNetSigningServer.Models
{
    public class AddAttachmentInput
    {
        public string PdfContent { get; set; } = "";
        public string AttachmentContent { get; set; } = "";
        public string FileName { get; set; } = "";
        public string? Description { get; set; }
        public string? MimeType { get; set; }
        public string? EncryptForCertificatePem { get; set; }
        public bool UseConfiguredEncryptionCertificate { get; set; } = false;
        public bool CompressBeforeEncrypt { get; set; } = true;

        /// <summary>AFRelationship of the file: Source, Data, Alternative, Supplement or Unspecified (default).</summary>
        public string? AfRelationship { get; set; }

        /// <summary>Refuse a document that already carries a signature (409 <c>PDF_ALREADY_SIGNED</c>).</summary>
        public bool RejectIfSigned { get; set; }
    }
}
