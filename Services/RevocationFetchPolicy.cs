using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace DotNetSigningServer.Services;

/// <summary>
/// What one LTV operation may cost on the network (#72), and what it may remember.
///
/// URLs of OCSP responders and CRL distribution points come from certificates the caller
/// handed us: signatures × chain × distribution points can add up to hundreds of fetches,
/// each with its own timeout, and a CRL is allowed to be as large as its issuer cares to
/// make it. So one operation gets a number of fetches and a window of time, and every
/// answer a size cap.
/// </summary>
internal static class RevocationFetchPolicy
{
    internal const int OcspMaxBytes = 64 * 1024;
    internal const int CrlMaxBytes = 32 * 1024 * 1024;
    internal const int TsaMaxBytes = 1024 * 1024;

    internal const int DefaultMaxFetches = 20;
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(30);

    private sealed class Budget
    {
        public int Remaining;
        public DateTime Until;
    }

    private static readonly AsyncLocal<Budget?> Current = new();

    /// <summary>Starts a budget for the current operation; dispose to end it.</summary>
    internal static IDisposable Begin(int maxFetches = DefaultMaxFetches, TimeSpan? window = null)
    {
        var previous = Current.Value;
        Current.Value = new Budget { Remaining = maxFetches, Until = DateTime.UtcNow + (window ?? DefaultWindow) };
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly Budget? _previous;
        public Scope(Budget? previous) => _previous = previous;
        public void Dispose() => Current.Value = _previous;
    }

    /// <summary>True when one more fetch is within the budget. Outside a budget scope there is no limit.</summary>
    internal static bool TryConsume()
    {
        var budget = Current.Value;
        if (budget is null) return true;
        if (DateTime.UtcNow > budget.Until) return false;
        return Interlocked.Decrement(ref budget.Remaining) >= 0;
    }

    // ── CRL cache: by URL, until the CRL says it is stale. ──────────────────

    private static readonly MemoryCache Cache = new(new MemoryCacheOptions { SizeLimit = 64L * 1024 * 1024 });
    internal static readonly TimeSpan MinTtl = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MaxTtl = TimeSpan.FromHours(6);
    internal static readonly TimeSpan UnknownTtl = TimeSpan.FromMinutes(15);

    internal static bool TryGetCrl(Uri url, out byte[] bytes)
    {
        if (Cache.TryGetValue(url.AbsoluteUri, out byte[]? hit) && hit is not null)
        {
            bytes = hit;
            return true;
        }
        bytes = Array.Empty<byte>();
        return false;
    }

    internal static void PutCrl(Uri url, byte[] bytes)
    {
        Cache.Set(url.AbsoluteUri, bytes, new MemoryCacheEntryOptions
        {
            Size = Math.Max(1, bytes.Length),
            AbsoluteExpirationRelativeToNow = TtlFor(bytes, DateTime.UtcNow),
        });
    }

    /// <summary>Until the CRL's nextUpdate, clamped; a CRL that cannot be read is kept for the default.</summary>
    internal static TimeSpan TtlFor(byte[] crl, DateTime now)
    {
        try
        {
            var parsed = new Org.BouncyCastle.X509.X509CrlParser().ReadCrl(crl);
            var next = parsed?.NextUpdate;
            if (next is null) return UnknownTtl;
            var ttl = next.Value - now;
            if (ttl < MinTtl) return MinTtl;
            return ttl > MaxTtl ? MaxTtl : ttl;
        }
        catch
        {
            return UnknownTtl;
        }
    }

    /// <summary>
    /// Reads a response body up to <paramref name="maxBytes"/>; null when it is larger.
    /// The Content-Length is looked at first, so an honest oversized answer is refused
    /// before a byte of it is read.
    /// </summary>
    internal static byte[]? ReadBounded(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        var declared = content.Headers.ContentLength;
        if (declared > maxBytes) return null;

        using var stream = content.ReadAsStream(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    internal static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        var declared = content.Headers.ContentLength;
        if (declared > maxBytes) return null;

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
