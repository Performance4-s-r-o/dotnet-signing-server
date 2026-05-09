namespace DotNetSigningServer.Options;

public class EvidenceOptions
{
    public bool Enabled { get; set; } = false;
    public string? EncryptionCertificatePem { get; set; }
    public string AttachmentFileName { get; set; } = "ses-evidence.p7m";
    public string MimeType { get; set; } = "application/pkcs7-mime";
    public bool CompressBeforeEncrypt { get; set; } = true;
}
