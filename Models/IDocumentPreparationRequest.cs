namespace DotNetSigningServer.Models
{
    /// <summary>
    /// Preparation of the document before it is signed (#184): presign, timestamp and visual-sign
    /// read the same three switches and run the same step (<c>DocumentPreparation</c>).
    /// </summary>
    public interface IDocumentPreparationRequest
    {
        /// <summary>Flatten the form and remove active content first; skipped on a signed document.</summary>
        bool FlattenForm { get; }
        /// <summary>Convert to PDF/A before signing. A signed document must already be PDF/A.</summary>
        bool ConvertToPdfa { get; }
        /// <summary>"PDF/A-2B" (default) or "PDF/A-3B"; anything else is refused.</summary>
        string? PdfaConformance { get; }
    }
}
