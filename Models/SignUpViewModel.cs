using System.ComponentModel.DataAnnotations;
using DotNetSigningServer.Services.Consents;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DotNetSigningServer.Models;

public class SignUpViewModel
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required]
    [Compare(nameof(Password))]
    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>
    /// The one consent checkbox (terms and DPA by default). Unchecked by default; checked on the
    /// server as well, so bypassing the HTML <c>required</c> does not help.
    /// </summary>
    [Range(typeof(bool), "true", "true")]
    public bool AcceptTerms { get; set; }

    /// <summary>Version of each consent document the form showed (hidden fields; Consents module Shadow or On).</summary>
    public Dictionary<string, int> ShownVersions { get; set; } = new();

    /// <summary>Content hash of each document the form showed (hidden fields); only compared, the server's hash is recorded.</summary>
    public Dictionary<string, string> ShownHashes { get; set; } = new();

    /// <summary>What the form shows; filled by the controller, never bound.</summary>
    [BindNever]
    public IReadOnlyList<ConsentDocumentVersion> Documents { get; set; } = [];

    /// <summary>Whether versions are shown and sent (Consents module Shadow or On).</summary>
    [BindNever]
    public bool ShowVersions { get; set; }
}
