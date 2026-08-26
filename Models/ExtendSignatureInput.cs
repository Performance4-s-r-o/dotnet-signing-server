namespace DotNetSigningServer.Models;

/// <summary>
/// Raises an already-signed PDF to PAdES B-LT, or B-LTA when an archive timestamp
/// is requested. Used both when a document is first enrolled for long-term
/// validity and on every later renewal.
/// </summary>
public class ExtendSignatureInput
{
    public string PdfContent { get; set; } = "";

    /// <summary>
    /// Whether to add an archive timestamp over the embedded validation data,
    /// which is what makes the result B-LTA rather than B-LT. Requires a TSA.
    ///
    /// A renewal always wants this: the point of re-running the operation years
    /// later is to lay down a fresh timestamp before the previous one's
    /// certificate expires.
    /// </summary>
    public bool AddArchiveTimestamp { get; set; } = true;

    /// <summary>
    /// RFC 3161 authority for the archive timestamp. The server has none of its
    /// own, so this is required whenever AddArchiveTimestamp is true.
    /// </summary>
    public string? TsaUrl { get; set; }
    public string? TsaUsername { get; set; }
    public string? TsaPassword { get; set; }
}
