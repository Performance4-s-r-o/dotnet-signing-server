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
    /// <summary>
    /// Structural PAdES level of this signature: B-B, B-T, B-LT, B-LTA,
    /// archive-timestamp or non-PAdES. Read per signature from the file, not a
    /// conformance verdict and never a statement of trust: no certificate chain
    /// is checked against a trust list.
    ///
    /// The levels build on each other. B-T needs a timestamp that verifies (its
    /// own token signed by the certificate it carries and stamping this
    /// signature's value, or a verified document timestamp in a later revision);
    /// a token that is present but fails either check counts as none. B-LT also
    /// needs validation data for this signature added after it
    /// (<see cref="ValidationDataRevision"/>), B-LTA a verified document
    /// timestamp over that validation data. Validation data added for a later
    /// signature does not raise an earlier one.
    /// </summary>
    public string StructuralLevel { get; set; } = "unknown";

    public string? SignerName { get; set; }
    public DateTime? SignedAt { get; set; }
    public string? DigestAlgorithm { get; set; }
    public string? SignatureAlgorithm { get; set; }
    public bool CoversWholeDocument { get; set; }
    public bool IntegrityVerified { get; set; }
    /// <summary>
    /// The raw /ByteRange array as the signature dictionary states it, or null
    /// when it is missing or not a list of integers.
    /// </summary>
    public long[]? ByteRange { get; set; }
    /// <summary>
    /// True when the ByteRange has the only acceptable shape: two ranges, the
    /// first starting at 0, the gap exactly this signature's /Contents hex
    /// string, the end inside the file. False means bytes outside /Contents are
    /// left unsigned, ranges overlap, or the range points past the file.
    /// </summary>
    public bool ByteRangeSane { get; set; }
    /// <summary>
    /// The entry of <see cref="SignatureInspectionResult.Revisions"/> whose end
    /// is this signature's signed end. Null when the ByteRange is not sane.
    /// Not the same numbering as <see cref="Revision"/>, which counts signatures.
    /// </summary>
    public int? FileRevision { get; set; }
    /// <summary>
    /// The entry of <see cref="SignatureInspectionResult.Revisions"/> that first
    /// carried validation data for this signature. Only set for B-LT and B-LTA.
    /// </summary>
    public int? ValidationDataRevision { get; set; }
    /// <summary>
    /// True when a timestamp token is present, verified or not. See
    /// <see cref="TimestampVerified"/> before saying the signature is timestamped.
    /// </summary>
    public bool HasTimestamp { get; set; }
    /// <summary>
    /// Internal consistency of the token only: its signature verifies with the
    /// certificate it carries itself (<see cref="TimestampSignatureValid"/>) and
    /// its messageImprint is the hash of what it claims to stamp
    /// (<see cref="TimestampMessageImprintMatches"/>). Anyone can make a token
    /// like that with a self-made certificate. It is NOT a statement that the
    /// time comes from a trusted authority: the certificate is not checked
    /// against any trust list, nor for revocation.
    /// </summary>
    public bool TimestampVerified { get; set; }
    /// <summary>
    /// The token's CMS signature, ESS certificate binding, critical timeStamping
    /// key usage and validity at genTime, checked against the certificate inside
    /// the token. Null when there is no token or it carries no certificate.
    /// </summary>
    public bool? TimestampSignatureValid { get; set; }
    /// <summary>
    /// For a signature timestamp: messageImprint equals the hash of the
    /// SignerInfo signature value. For a document timestamp: the hash of its
    /// signed byte ranges. Null when there is nothing to compare against, or
    /// when the imprint uses a hash weaker than SHA-256 (not evaluated).
    /// </summary>
    public bool? TimestampMessageImprintMatches { get; set; }
    public DateTime? TimestampedAt { get; set; }
    /// <summary>
    /// Who the token SAYS signed it — the subject of the embedded signer
    /// certificate, or the TSA name inside TSTInfo when no certificate travels
    /// with the token.
    ///
    /// Asserted by the document, not proven by us. Nothing here validates a
    /// trust chain, and the bytes come from whatever file the caller uploaded,
    /// so a hand-made token can carry any subject its author likes. It is still
    /// worth reporting — it is the only place the document itself names an
    /// authority, where the request-time configuration only says who was asked
    /// — but a caller must treat it as a claim to check, never as a verdict.
    /// </summary>
    public string? TimestampAuthority { get; set; }
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
    /// <summary>SHA-256 (lowercase hex) of the whole file as received.</summary>
    public string DocumentSha256 { get; set; } = "";
    public long ByteLength { get; set; }
    /// <summary>
    /// Every incremental revision of the file, oldest first. A revision ends
    /// where a signature's ByteRange ends or after a %%EOF that closes a trailer
    /// (its startxref points at an xref table or xref stream).
    /// </summary>
    public List<InspectedRevision> Revisions { get; set; } = new();
    /// <summary>
    /// Bytes after the end of the last revision, which belong to none of them.
    /// Usually 0. More means something was appended after the last %%EOF (or
    /// an update without a valid trailer): <see cref="DocumentSha256"/> then
    /// differs from the last revision's hash, and those bytes are not covered
    /// by any signature.
    /// </summary>
    public long TrailingBytes { get; set; }
}

/// <summary>One incremental revision: the file's first <see cref="EndOffset"/> bytes.</summary>
public class InspectedRevision
{
    /// <summary>1-based, oldest first.</summary>
    public int Revision { get; set; }
    /// <summary>Exclusive end offset of the revision in the file.</summary>
    public long EndOffset { get; set; }
    /// <summary>SHA-256 (lowercase hex) of bytes [0, EndOffset).</summary>
    public string Sha256 { get; set; } = "";
}
