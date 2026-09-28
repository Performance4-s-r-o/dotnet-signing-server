namespace DotNetSigningServer.Services.Email;

/// <summary>What a message is, besides its content: sent as tags, and decides <c>critical</c>.</summary>
/// <param name="TemplateId">One of <see cref="EmailTemplateId"/>.</param>
/// <param name="Locale">Language the message was rendered in (e.g. <c>cs</c>).</param>
/// <param name="UserId">Recipient's account, so bounces can be matched back to it.</param>
/// <param name="Critical">
/// Forces <c>critical</c>; otherwise it follows the template (<see cref="CriticalEmails"/>).
/// </param>
public sealed record EmailSendOptions(string? TemplateId, string? Locale, Guid? UserId = null, bool Critical = false)
{
    /// <summary>Critical when forced or when the template is one of <see cref="CriticalEmails"/>.</summary>
    public bool IsCritical => Critical || CriticalEmails.IsCritical(TemplateId);
}
