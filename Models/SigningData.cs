using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models
{
    public class SigningData
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string PresignedPdfPath { get; set; } = "";
        public string HashToSign { get; set; } = "";
        public string CertificatePem { get; set; } = "";
        public string FieldName { get; set; } = "Signature1";
        public string? TsaUrl { get; set; }
        public string? TsaUsername { get; set; }
        public string? TsaPassword { get; set; }
        public Guid? UserId { get; set; }
    }
}
