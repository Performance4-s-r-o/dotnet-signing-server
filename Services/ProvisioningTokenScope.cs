namespace DotNetSigningServer.Services;

/// <summary>
/// What the integrator's provisioning token may do (#131): create and list its provisioned
/// accounts, and read the capability list. Everything else is 403 <c>TOKEN_SCOPE</c>, so a leaked
/// main key signs nothing and opens no tenant's data. The provisioning routes refuse every other
/// token the same way.
/// </summary>
public static class ProvisioningTokenScope
{
    public const string PathPrefix = "/api/provisioning";
    public const string AccountsPath = "/api/provisioning/accounts";

    private static readonly (string Method, string Path)[] Allowed =
    {
        ("POST", AccountsPath),
        ("GET", AccountsPath),
        ("GET", "/api/capabilities"),
    };

    public static bool IsProvisioningPath(PathString path)
    {
        var value = path.Value ?? string.Empty;
        return value.Equals(PathPrefix, StringComparison.OrdinalIgnoreCase)
               || value.StartsWith(PathPrefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowed(string method, PathString path)
    {
        var value = (path.Value ?? string.Empty).TrimEnd('/');
        foreach (var (m, p) in Allowed)
        {
            if (string.Equals(method, m, StringComparison.OrdinalIgnoreCase)
                && string.Equals(value, p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
