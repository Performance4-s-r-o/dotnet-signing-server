namespace DotNetSigningServer.Options;

/// <summary>
/// Who the portal is, and where it may call from (#78). docker-compose passes the raw names
/// (<c>INTERNAL_API_PORT</c>, <c>TRUSTED_CALLER_USER_IDS</c>); see <see cref="EnvValues"/>.
///
/// <list type="bullet">
/// <item><see cref="InternalPort"/> opens a second listener meant for the Docker network only.
/// A token of a <see cref="TrustedCallerUserIds"/> account is accepted there and nowhere else: on the
/// public port it is a 401, as if it did not exist. Unset = one listener, nothing changes.</item>
/// <item>Only a portal token on the internal port (or, without one, any portal token) is a
/// <i>trusted caller</i>: its <c>X-P4-Tenant</c> / <c>X-Request-Id</c> are believed and it alone
/// may skip the attachment debit. Every other caller's copy of those headers is ignored.</item>
/// </list>
/// </summary>
public class PortalTrustOptions
{
    public const string InternalPortVariable = "INTERNAL_API_PORT";
    public const string TrustedCallerUserIdsVariable = "TRUSTED_CALLER_USER_IDS";

    /// <summary>The internal listener's port; null = no internal listener.</summary>
    public int? InternalPort { get; set; }

    /// <summary>The trusted caller's service account(s).</summary>
    public HashSet<Guid> TrustedCallerUserIds { get; set; } = new();

    /// <summary>True when ids are set without an internal port: the trusted token then works on every port.</summary>
    public bool TrustedOnEveryPort => InternalPort == null && TrustedCallerUserIds.Count > 0;

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

        var ids = ParseIds(TrustedCallerUserIdsVariable, configuration[TrustedCallerUserIdsVariable]);

        // An internal port without knowing whose token it is for would restrict nothing and
        // trust nobody — a protection that looks switched on and is not.
        if (port != null && ids.Count == 0)
        {
            throw new InvalidOperationException(
                $"{InternalPortVariable} is set but {TrustedCallerUserIdsVariable} is empty. Set {TrustedCallerUserIdsVariable} to the "
                + "trusted caller's service account id(s), or unset the port.");
        }

        return new PortalTrustOptions { InternalPort = port, TrustedCallerUserIds = ids };
    }

    private static HashSet<Guid> ParseIds(string name, string? raw)
    {
        var ids = new HashSet<Guid>();
        if (EnvValues.IsUnset(raw)) return ids;
        foreach (var entry in raw!.Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Guid.TryParse(entry.Trim(), out var id) || id == Guid.Empty)
            {
                throw new InvalidOperationException($"{name} must list user ids (GUIDs), got '{entry}'.");
            }
            ids.Add(id);
        }
        return ids;
    }
}
