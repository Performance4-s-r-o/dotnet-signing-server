using DotNetSigningServer.Services.Backoffice.Consents;

namespace DotNetSigningServer.Services.Consents;

/// <summary>Which published sentence a form with one consent checkbox may show.</summary>
public static class ConsentPromptSelection
{
    /// <summary>
    /// The sign-up form has a single checkbox covering every document a new account must agree
    /// to. A published sentence can stand in for it only when there is exactly one required
    /// prompt: two would mean two separate decisions, and showing them under one checkbox would
    /// record a consent nobody gave separately. Anything else falls back to this app's own
    /// wording, which is what shipped before the service published any.
    /// </summary>
    public static BackofficeConsentPrompt? ForSingleCheckbox(IReadOnlyList<BackofficeConsentPrompt>? prompts)
    {
        if (prompts is null) return null;
        BackofficeConsentPrompt? only = null;
        foreach (var prompt in prompts)
        {
            if (!prompt.Required) continue;
            if (only != null) return null;
            only = prompt;
        }
        return only;
    }
}
