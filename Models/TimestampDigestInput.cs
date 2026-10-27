namespace DotNetSigningServer.Models
{
    /// <summary>RFC 3161 timestamp over a SHA-256 digest the caller already holds; no document is sent.</summary>
    public class TimestampDigestInput
    {
        /// <summary>SHA-256 digest, exactly 32 bytes: 64 hex characters or base64.</summary>
        public string Digest { get; set; } = string.Empty;
        public string? TsaUrl { get; set; }
        public string? TsaUsername { get; set; }
        public string? TsaPassword { get; set; }
        /// <summary>Tried when TsaUrl fails (network, HTTP or a rejected response).</summary>
        public string? TsaBackupUrl { get; set; }
        public string? TsaBackupUsername { get; set; }
        public string? TsaBackupPassword { get; set; }
    }

    public class TimestampDigestResult
    {
        /// <summary>The RFC 3161 TimeStampToken (CMS SignedData, DER), base64.</summary>
        public string Token { get; set; } = string.Empty;
        public DateTime GenTime { get; set; }
        /// <summary>Subject of the certificate the authority signed the token with.</summary>
        public string? TsaSubject { get; set; }
        public string DigestAlgorithm { get; set; } = "SHA-256";
    }
}
