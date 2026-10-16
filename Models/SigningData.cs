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
        /// <summary>Tried when TsaUrl fails (network, HTTP or a rejected response).</summary>
        public string? TsaBackupUrl { get; set; }
        public string? TsaBackupUsername { get; set; }
        public string? TsaBackupPassword { get; set; }
        public Guid? UserId { get; set; }
        /// <summary>The portal's tenant (<c>X-P4-Tenant</c>, #78); null for other callers and older presigns.</summary>
        [MaxLength(64)]
        public string? TenantId { get; set; }
        /// <summary>The portal's <c>X-Request-Id</c> of the presign (#78).</summary>
        [MaxLength(128)]
        public string? RequestId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
