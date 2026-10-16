using DotNetSigningServer.Options;

namespace DotNetSigningServer.Services;

/// <summary>What a token is, given the port it came in on (#78).</summary>
public enum PortalCallKind
{
    /// <summary>Not the portal: a customer's API token, a browser token.</summary>
    Ordinary,
    /// <summary>The portal, where it is supposed to call from.</summary>
    Trusted,
    /// <summary>The portal's token on the public listener: refused as if it did not exist.</summary>
    Refused,
}

/// <summary>Tenant and request id the portal sent; both null for every other caller.</summary>
public sealed record PortalCaller(string? TenantId, string? RequestId)
{
    public static readonly PortalCaller None = new(null, null);
}

/// <summary>
/// The one place that decides whether a request comes from the portal (#78) and what its
/// <c>X-P4-Tenant</c> / <c>X-Request-Id</c> say. See <see cref="PortalTrustOptions"/>.
/// </summary>
public static class PortalTrust
{
    public const string TrustedCallerClaim = "p4:trusted_caller";
    public const string TenantHeader = "X-P4-Tenant";
    public const string RequestIdHeader = "X-Request-Id";

    /// <summary>Sizes of the columns on SigningData / UsageRecord.</summary>
    public const int MaxTenantIdLength = 64;
    public const int MaxRequestIdLength = 128;

    public static PortalCallKind Classify(PortalTrustOptions options, int localPort, Guid userId, bool isBrowserToken)
    {
        // A browser token is used from a browser, through the public hostname, whoever issued it.
        if (isBrowserToken || !options.PortalUserIds.Contains(userId)) return PortalCallKind.Ordinary;
        if (options.InternalPort is int internalPort && localPort != internalPort) return PortalCallKind.Refused;
        return PortalCallKind.Trusted;
    }

    public static bool IsTrustedCaller(HttpContext context) =>
        context.User?.Identity?.IsAuthenticated == true
        && context.User.FindFirst(TrustedCallerClaim)?.Value == "true";

    /// <summary>
    /// The portal's headers, sanitised. Anybody else's are ignored: a customer writing a tenant
    /// id would otherwise get rows attributed to somebody else's tenant.
    /// </summary>
    public static PortalCaller CallerOf(HttpContext? context)
    {
        if (context == null || !IsTrustedCaller(context)) return PortalCaller.None;
        return new PortalCaller(
            Clean(context.Request.Headers[TenantHeader].ToString(), MaxTenantIdLength),
            Clean(context.Request.Headers[RequestIdHeader].ToString(), MaxRequestIdLength));
    }

    /// <summary>
    /// Two tenants that are both known and differ (#78). One side unknown (an older row, a caller
    /// that is not the portal) is not a conflict: there is nothing to compare.
    /// </summary>
    public static bool TenantsConflict(string? recorded, string? caller) =>
        recorded != null && caller != null && !string.Equals(recorded, caller, StringComparison.Ordinal);

    /// <summary>A value from a safe alphabet within the length, or null: it goes into logs and columns.</summary>
    internal static string? Clean(string? raw, int maxLength)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > maxLength) return null;
        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')) return null;
        }
        return value;
    }
}
