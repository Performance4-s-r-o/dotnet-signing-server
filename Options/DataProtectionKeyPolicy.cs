namespace DotNetSigningServer.Options;

/// <summary>
/// What to do when the DataProtection key ring has no certificate. The ring also protects stored
/// TSA credentials, so in Production an unencrypted ring is refused unless
/// <c>DATA_PROTECTION_ALLOW_PLAINTEXT_KEYS=true</c> says it is on purpose (single host, volume
/// protected by the host). Outside Production it is only a warning.
/// </summary>
public static class DataProtectionKeyPolicy
{
    public sealed record Outcome(bool Refuse)
    {
        public string Message(string path) => Refuse
            ? $"The DataProtection key ring at {path} would be stored unencrypted. Set DataProtection__CertificateBase64 "
              + "(+ __CertificatePassword) to encrypt it, or DATA_PROTECTION_ALLOW_PLAINTEXT_KEYS=true to accept plaintext keys on purpose."
            : $"DataProtection key ring is stored unencrypted at {path}. Set DataProtection__CertificateBase64 "
              + "(+ __CertificatePassword) to encrypt it; the ring also protects stored TSA credentials.";
    }

    /// <summary>Null = nothing to say (not Production, or plaintext allowed on purpose and logged elsewhere).</summary>
    public static Outcome? Evaluate(bool isProduction, bool migrateOnly, string? allowPlaintext)
    {
        if (!isProduction || migrateOnly) return null;
        var allowed = string.Equals(allowPlaintext?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        return new Outcome(Refuse: !allowed);
    }
}
