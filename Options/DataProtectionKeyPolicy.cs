namespace DotNetSigningServer.Options;

/// <summary>
/// What to do when the DataProtection key ring has no certificate. The ring also protects stored
/// TSA credentials, so in Production an unencrypted ring is refused unless
/// <c>DATA_PROTECTION_ALLOW_PLAINTEXT_KEYS=true</c> says it is on purpose (single host, volume
/// protected by the host); that case starts with a warning. Outside Production nothing is checked
/// or printed (development, tests, the <c>migrate</c> step).
/// </summary>
public static class DataProtectionKeyPolicy
{
    public sealed record Outcome(bool Refuse)
    {
        public string Message(string path) => Refuse
            ? $"The DataProtection key ring at {path} would be stored unencrypted. Set DataProtection__CertificateBase64 "
              + "(+ __CertificatePassword; in docker-compose DATA_PROTECTION_CERTIFICATE_BASE64 / DATA_PROTECTION_CERTIFICATE_PASSWORD, "
              + "Coolify shared DOTNET_DATA_PROTECTION_CERTIFICATE_BASE64 / _PASSWORD) to encrypt it, or "
              + "DATA_PROTECTION_ALLOW_PLAINTEXT_KEYS=true (Coolify shared DOTNET_DATA_PROTECTION_ALLOW_PLAINTEXT_KEYS) to accept plaintext keys on purpose."
            : $"DataProtection key ring is stored unencrypted at {path}. Set DataProtection__CertificateBase64 "
              + "(+ __CertificatePassword) to encrypt it; the ring also protects stored TSA credentials.";
    }

    /// <summary>Null = nothing to say (not Production, or the <c>migrate</c> step).</summary>
    public static Outcome? Evaluate(bool isProduction, bool migrateOnly, string? allowPlaintext)
    {
        if (!isProduction || migrateOnly) return null;
        var allowed = string.Equals(allowPlaintext?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        return new Outcome(Refuse: !allowed);
    }
}
