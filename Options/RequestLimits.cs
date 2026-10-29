using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace DotNetSigningServer.Options;

/// <summary>
/// Slow-request protection of the listeners (#72). Kestrel's defaults (30 s for headers, 240 B/s
/// for the body) let a 40 MB upload take days. Defaults: headers within 15 s, body at least
/// 20 KB/s after a 10 s grace. Overridable by <c>Limits:RequestHeadersTimeoutSeconds</c>,
/// <c>Limits:MinRequestBodyBytesPerSecond</c> and <c>Limits:MinRequestBodyGraceSeconds</c>.
/// </summary>
public static class RequestLimits
{
    public static void Apply(KestrelServerLimits limits, IConfiguration configuration)
    {
        limits.RequestHeadersTimeout = TimeSpan.FromSeconds(
            configuration.GetValue<int?>("Limits:RequestHeadersTimeoutSeconds") ?? 15);
        limits.MinRequestBodyDataRate = new MinDataRate(
            bytesPerSecond: configuration.GetValue<int?>("Limits:MinRequestBodyBytesPerSecond") ?? 20 * 1024,
            gracePeriod: TimeSpan.FromSeconds(configuration.GetValue<int?>("Limits:MinRequestBodyGraceSeconds") ?? 10));
    }
}
