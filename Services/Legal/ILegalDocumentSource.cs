namespace DotNetSigningServer.Services.Legal;

/// <summary>
/// Where <c>LegalController</c> gets a legal document from. Chosen by
/// <c>P4Backoffice:Modules:Docs</c>: Off → <see cref="DbLegalDocumentSource"/> (snapshot only),
/// On → <see cref="BackofficeLegalDocumentSource"/> (service, then snapshot).
/// </summary>
public interface ILegalDocumentSource
{
    /// <summary>
    /// The document in force for <paramref name="slug"/> in <paramref name="locale"/>, else in
    /// English; null when there is none (the caller renders the "not available" page).
    /// Never throws and never waits for the P4 Backoffice service.
    /// </summary>
    Task<LegalDocumentRendered?> GetAsync(string slug, string locale, CancellationToken cancellationToken = default);
}
