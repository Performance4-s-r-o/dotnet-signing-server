namespace DotNetSigningServer.Services.Email;

public interface IEmailTemplateRenderer
{
    /// <summary>
    /// Render a template to its final (subject, html) pair.
    /// Falls back to the default locale ("en") if the requested locale isn't available.
    /// </summary>
    EmailTemplateResult Render(string templateId, string locale, IReadOnlyDictionary<string, string?>? variables);
}

public sealed record EmailTemplateResult(string Subject, string HtmlBody);
