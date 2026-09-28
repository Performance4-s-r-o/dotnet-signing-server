using DotNetSigningServer.Services.Consents;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DotNetSigningServer.Models;

/// <summary>The <c>/Account/Consent</c> page: documents a signed-in user still has to confirm.</summary>
public class ConsentViewModel
{
    /// <summary>Confirmation checkbox; required when a document needs consent (not just acknowledgement).</summary>
    public bool Accept { get; set; }

    public string? ReturnUrl { get; set; }

    public Dictionary<string, int> ShownVersions { get; set; } = new();

    public Dictionary<string, string> ShownHashes { get; set; } = new();

    [BindNever]
    public IReadOnlyList<ConsentDocumentVersion> Documents { get; set; } = [];

    /// <summary>The user has no consent records yet (registered before they were kept).</summary>
    [BindNever]
    public bool Initial { get; set; }

    [BindNever]
    public bool RequiresCheckbox => Documents.Any(d => d.Action == ConsentActions.Granted);
}
