using Microsoft.AspNetCore.DataProtection;

namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <summary>
/// Encrypts outbox payloads at rest. Payloads can carry one-time codes and reset links,
/// which must not be readable from a database dump. Uses the application's persisted Data
/// Protection key ring (see <c>Program.cs</c>), so items survive restarts.
/// </summary>
public sealed class OutboxPayloadProtector
{
    public const string Purpose = "P4Backoffice.Outbox.v1";

    private readonly IDataProtector _protector;

    public OutboxPayloadProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string payloadJson) => _protector.Protect(payloadJson);

    public string Unprotect(string protectedPayload) => _protector.Unprotect(protectedPayload);
}
