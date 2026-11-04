namespace DotNetSigningServer.Options;

/// <summary>
/// The optional second listener for the Docker network (<c>INTERNAL_API_PORT</c>, #78, #131).
/// When it is set, a provisioning token is accepted there only (401 elsewhere) and
/// <c>/internal/concurrency</c> is served there. Every other token works on both listeners.
/// Unset = one listener; the provisioning token then works on it.
/// </summary>
public class InternalListenerOptions
{
    public const string InternalPortVariable = "INTERNAL_API_PORT";

    /// <summary>The internal listener's port; null = no internal listener.</summary>
    public int? InternalPort { get; set; }

    /// <summary>Whether a request came in on the internal listener.</summary>
    public bool IsInternal(int localPort) => InternalPort is int port && localPort == port;

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

    public static InternalListenerOptions From(IConfiguration configuration)
    {
        var port = EnvValues.Int(configuration, InternalPortVariable);
        if (port is <= 0 or > 65535)
        {
            throw new InvalidOperationException($"{InternalPortVariable} must be a TCP port (1-65535), got '{port}'.");
        }

        return new InternalListenerOptions
        {
            InternalPort = port,
        };
    }
}
