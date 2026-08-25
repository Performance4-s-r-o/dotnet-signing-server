namespace DotNetSigningServer.Options;

/// <summary>
/// Where this deployment lives. The public origin comes from configuration
/// (<c>FqdnServerName</c> / <c>SERVICE_FQDN_SERVER</c>) and nowhere else — never
/// from a hardcoded domain, and never guessed from the incoming request, so a
/// stray Host header cannot redirect users or leak into an e-mail.
/// </summary>
public class AppOptions
{
    /// <summary>
    /// Public origin of this deployment. Accepts a full URL
    /// (<c>https://app.performance4pdf.com</c>) or a bare host (<c>app.performance4pdf.com</c>,
    /// <c>localhost:5000</c>) — see <see cref="BaseUrl"/> for how a missing
    /// scheme is filled in.
    /// </summary>
    public string? FqdnServerName { get; set; }

    /// <summary>
    /// The normalised absolute origin, without a trailing slash.
    ///
    /// A bare host gets <c>https://</c>, except loopback hosts, which get
    /// <c>http://</c> so local development works without a certificate. This
    /// matters because Coolify hands over <c>SERVICE_FQDN_*</c> without a
    /// scheme, and a scheme-less origin produces links that browsers and mail
    /// clients treat as relative — dead links in transactional e-mail.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// When the origin is not configured. Program.cs checks this at startup so a
    /// misconfigured deployment fails immediately instead of quietly sending
    /// broken e-mails.
    /// </exception>
    public string BaseUrl
    {
        get
        {
            var configured = FqdnServerName?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(configured))
            {
                throw new InvalidOperationException(
                    "FqdnServerName is not configured. Set SERVICE_FQDN_SERVER (or App FqdnServerName) "
                    + "to this deployment's public origin, e.g. https://app.performance4pdf.com.");
            }

            if (configured.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || configured.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return configured;
            }

            var isLoopback = configured.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                             || configured.StartsWith("127.0.0.1", StringComparison.Ordinal)
                             || configured.StartsWith("[::1]", StringComparison.Ordinal);

            return $"{(isLoopback ? "http" : "https")}://{configured}";
        }
    }

    /// <summary>
    /// Absolute URL for an application-relative path, e.g.
    /// <c>AbsoluteUrl("/Billing")</c>.
    /// </summary>
    public string AbsoluteUrl(string path)
    {
        if (string.IsNullOrEmpty(path)) return BaseUrl;
        return path.StartsWith('/') ? $"{BaseUrl}{path}" : $"{BaseUrl}/{path}";
    }
}
