using System.Collections.Concurrent;
using DotNetSigningServer.Options;
using Microsoft.AspNetCore.Http;

namespace DotNetSigningServer.Services;

/// <summary>
/// Spots a caller on the internal listener that sends the trusted caller's headers without the
/// account flag (#78), typically after a database rebuild. Its tenant headers are then ignored,
/// so tenant checks are off and per-account limits apply; this makes that loud, at most once a
/// minute per account. Header values never reach the log.
/// </summary>
public sealed class MissingTrustedCallerFlag
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const int MaxTracked = 1000;

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastWarned = new();

    /// <summary>The headers only the trusted caller sends.</summary>
    public static bool SendsTrustedCallerHeaders(IHeaderDictionary headers) =>
        headers.ContainsKey(PortalTrust.TenantHeader)
        || headers.ContainsKey(Controllers.PdfSigningApiController.AttachmentDebitBypassHeader);

    /// <summary>True when this request deserves a warning now (and records that it got one).</summary>
    public bool ShouldWarn(
        PortalTrustOptions options, int localPort, PortalCallKind kind, bool isBrowserToken,
        Guid userId, IHeaderDictionary headers, DateTimeOffset now)
    {
        if (kind != PortalCallKind.Ordinary || isBrowserToken) return false;
        if (options.InternalPort is not int internalPort || localPort != internalPort) return false;
        if (!SendsTrustedCallerHeaders(headers)) return false;

        if (_lastWarned.TryGetValue(userId, out var last) && now - last < Interval) return false;
        // Bounded: the internal port is not public, but the map must not grow without end.
        if (_lastWarned.Count >= MaxTracked) _lastWarned.Clear();
        _lastWarned[userId] = now;
        return true;
    }
}
