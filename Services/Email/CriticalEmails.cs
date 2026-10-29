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

    /// <summary>
    /// How long the content of a critical message is of use (matches the code or link it carries);
    /// after it nobody sends it. An unknown critical template gets an hour.
    /// </summary>
    public static TimeSpan ValidFor(string? templateId) => templateId switch
    {
        EmailTemplateId.TwoFactorCode => TimeSpan.FromMinutes(10),
        EmailTemplateId.PasswordReset => TimeSpan.FromHours(1),
        EmailTemplateId.EmailVerification => TimeSpan.FromHours(20),
        _ => TimeSpan.FromHours(1),
    };

    public static bool IsCritical(string? templateId) => templateId != null && TemplateIds.Contains(templateId);
}
