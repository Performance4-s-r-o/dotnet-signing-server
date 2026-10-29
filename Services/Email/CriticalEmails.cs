namespace DotNetSigningServer.Services.Email;

/// <summary>
/// Messages a user is waiting for to get into their account. Sent as <c>critical</c> (the
/// service bypasses complaint/manual suppression for them).
/// </summary>
public static class CriticalEmails
{
    public static readonly IReadOnlySet<string> TemplateIds = new HashSet<string>(StringComparer.Ordinal)
    {
        EmailTemplateId.TwoFactorCode,
        EmailTemplateId.PasswordReset,
        EmailTemplateId.EmailVerification,
    };

    public static bool IsCritical(string? templateId) => templateId != null && TemplateIds.Contains(templateId);
}
