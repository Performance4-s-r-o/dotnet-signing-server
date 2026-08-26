namespace DotNetSigningServer.Models;

public class InspectSignaturesInput
{
    public string PdfContent { get; set; } = "";
}

/// <summary>One signature field found in the document.</summary>
public class SignatureInspectionEntry
{
    public string FieldName { get; set; } = "";
    /// <summary>PDF subfilter, e.g. ETSI.CAdES.detached or adbe.pkcs7.detached.</summary>
    public string? SubFilter { get; set; }
    /// <summary>True for an RFC 3161 document (archive) timestamp rather than a signature.</summary>
    public bool IsDocumentTimestamp { get; set; }
    /// <summary>Structural PAdES level: B-B, B-T, B-LT, B-LTA, archive-timestamp or non-PAdES.</summary>
    public string Level { get; set; } = "unknown";
    public string? SignerName { get; set; }
    public DateTime? SignedAt { get; set; }
    public string? DigestAlgorithm { get; set; }
    public string? SignatureAlgorithm { get; set; }
    public bool CoversWholeDocument { get; set; }
    public bool IntegrityVerified { get; set; }
    public bool HasTimestamp { get; set; }
    public DateTime? TimestampedAt { get; set; }
    /// <summary>
    /// Expiry of the certificate the timestamp authority used. A B-LTA document
    /// must be re-timestamped before this date: afterwards no fresh revocation
    /// data can be obtained for the token and the evidence chain cannot be repaired.
    /// </summary>
    public DateTime? TimestampCertificateNotAfter { get; set; }
    /// <summary>
    /// Start of the timestamp certificate's validity. Together with NotAfter it
    /// gives the certificate's lifespan, which is what a renewal schedule needs:
    /// a safety margin has to scale with how long the certificate lives, not be a
    /// fixed number of months.
    /// </summary>
    public DateTime? TimestampCertificateNotBefore { get; set; }
    public DateTime? SigningCertificateNotAfter { get; set; }
    /// <summary>
    /// Which incremental revision of the file this field appeared in. Signatures
    /// added after an archive timestamp sit in a later revision than it does, and
    /// are therefore outside what it protects.
    /// </summary>
    public int Revision { get; set; }
}

public class SignatureInspectionResult
{
    public List<SignatureInspectionEntry> Signatures { get; set; } = new();
    /// <summary>Whether the document carries a DSS dictionary (embedded validation data).</summary>
    public bool HasDss { get; set; }
    public int ArchiveTimestampCount { get; set; }
    /// <summary>
    /// The earliest timestamp-certificate expiry in the document — the deadline a
    /// renewal schedule has to be planned against. Null when nothing is timestamped.
    /// </summary>
    public DateTime? EarliestTimestampCertificateExpiry { get; set; }
    /// <summary>
    /// True when a signature was added after the newest archive timestamp, so
    /// nothing is preserving it.
    ///
    /// It happens naturally whenever a maintained document is signed again — a
    /// second approver on a contract already enrolled. The document is not
    /// broken, but that last signature stays unprotected until the next archive
    /// timestamp covers it, which is a reason to renew now rather than in years.
    /// </summary>
    public bool HasUnprotectedSignature { get; set; }
}
