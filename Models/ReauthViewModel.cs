namespace DotNetSigningServer.Models;

/// <summary>Step-up re-authentication form (#80): password, or a code sent by e-mail.</summary>
public class ReauthViewModel
{
    /// <summary>Local page to return to once confirmed (the page the action was started from).</summary>
    public string? ReturnUrl { get; set; }

    public string? Password { get; set; }

    public string? Code { get; set; }

    /// <summary>A code has been e-mailed for this confirmation.</summary>
    public bool CodeSent { get; set; }

    /// <summary>Shown because an action was refused (403), not because the user opened the page.</summary>
    public bool Required { get; set; }
}
