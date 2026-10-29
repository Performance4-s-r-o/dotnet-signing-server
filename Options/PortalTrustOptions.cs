namespace DotNetSigningServer.Options;

/// <summary>
/// Where a trusted caller may call from (#78). docker-compose passes the raw name
/// (<c>INTERNAL_API_PORT</c>); see <see cref="EnvValues"/>. Which account is trusted is a flag on
/// the account (<c>User.TrustedCaller</c>), set by an administrator.
///
/// <list type="bullet">
/// <item><see cref="InternalPort"/> opens a second listener meant for the Docker network only.
/// A token of a flagged account is accepted there and nowhere else: on the public port it is a
/// 401, as if it did not exist. Unset = one listener and no trusted caller at all.</item>
/// <item>Only a flagged account's server token on the internal port is a <i>trusted caller</i>:
/// its <c>X-P4-Tenant</c> / <c>X-Request-Id</c> are believed and it alone may skip the attachment
/// debit. Every other caller's copy of those headers is ignored.</item>
/// </list>
/// </summary>
public class PortalTrustOptions
{
    public const string InternalPortVariable = "INTERNAL_API_PORT";

    /// <summary>Removed: the trusted account is a flag in the administration now. Read only to warn.</summary>
    public const string RemovedTrustedCallerUserIdsVariable = "TRUSTED_CALLER_USER_IDS";

    /// <summary>The internal listener's port; null = no internal listener.</summary>
    public int? InternalPort { get; set; }

    /// <summary>True when the removed id list is still set: it is ignored, and the start says so.</summary>
    public bool RemovedVariableSet { get; set; }

    /// <summary>
    /// The listen URLs with the internal port added (once). Empty <paramref name="urls"/> means
    /// Kestrel's default, which an explicit list would otherwise replace.
    /// </summary>
    public static string WithInternalListener(string? urls, int internalPort)
    {
        var listening = (string.IsNullOrWhiteSpace(urls) ? "http://localhost:5000" : urls)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var already = listening.Any(u =>
            Uri.TryCreate(u.Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0"), UriKind.Absolute, out var uri)
            && uri.Port == internalPort);
        if (!already) listening.Add($"http://+:{internalPort}");
        return string.Join(';', listening);
    }

    public static PortalTrustOptions From(IConfiguration configuration)
    {
        var port = EnvValues.Int(configuration, InternalPortVariable);
        if (port is <= 0 or > 65535)
        {
            throw new InvalidOperationException($"{InternalPortVariable} must be a TCP port (1-65535), got '{port}'.");
        }

        return new PortalTrustOptions
        {
            InternalPort = port,
            RemovedVariableSet = !EnvValues.IsUnset(configuration[RemovedTrustedCallerUserIdsVariable]),
        };
    }
}
