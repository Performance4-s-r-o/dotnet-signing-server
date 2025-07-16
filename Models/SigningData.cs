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
    }
}
