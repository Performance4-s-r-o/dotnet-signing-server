using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services;

/// <summary>
/// Best-effort Loki shipper. Entries are queued and flushed in batches by a
/// background timer, so high-volume ILogger output doesn't translate into one
/// HTTP request per line. Failures are swallowed — logging must never break a
/// request.
/// </summary>
public class LokiClient : IDisposable
{
    private readonly record struct Entry(
        long TimestampNs,
        string Level,
        string Message,
        IReadOnlyDictionary<string, string>? Labels);

    private const int MaxBatch = 200;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LokiOptions _options;
    private readonly ConcurrentQueue<Entry> _queue = new();
    private readonly Timer? _flushTimer;

    public LokiClient(IHttpClientFactory httpClientFactory, IOptions<LokiOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;

        if (!string.IsNullOrWhiteSpace(_options.Url))
        {
            _flushTimer = new Timer(
                _ => { _ = FlushAsync(); },
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));
        }
    }

    private string ResolvedEnvironment =>
        _options.Environment
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? "production";

    /// <summary>
    /// Builds the Loki push endpoint. <see cref="LokiOptions.Url"/> is the
    /// deployment's Loki ingress (e.g. a Grafana datasource proxy at
    /// <c>…/datasource/loki</c>). The canonical <c>/loki/api/v1/push</c> suffix
    /// is always appended verbatim — even when the URL already ends in
    /// <c>/loki</c>, the path behind the proxy still expects its own
    /// <c>/loki/api/v1/push</c>, so the doubled <c>…/loki/loki/api/v1/push</c>
    /// is intentional and correct for this setup.
    /// </summary>
    internal static string BuildPushUrl(string baseUrl)
        => baseUrl.TrimEnd('/') + "/loki/api/v1/push";

    public void Enqueue(string level, string message, IReadOnlyDictionary<string, string>? labels = null)
    {
        if (string.IsNullOrWhiteSpace(_options.Url))
        {
            return;
        }

        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000; // nanoseconds
        _queue.Enqueue(new Entry(ts, level, message, labels));

        if (_queue.Count >= MaxBatch)
        {
            _ = FlushAsync();
        }
    }

    public Task LogAsync(string level, string message, IDictionary<string, string>? labels = null)
    {
        Enqueue(level, message, labels is null ? null : new Dictionary<string, string>(labels));
        return Task.CompletedTask;
    }

    public Task LogExceptionAsync(Exception ex, string? path = null, string? traceId = null, string? userId = null)
    {
        var labels = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(path))
        {
            labels["path"] = path;
        }
        if (!string.IsNullOrWhiteSpace(traceId))
        {
            labels["traceId"] = traceId;
        }
        if (!string.IsNullOrWhiteSpace(userId))
        {
            labels["userId"] = userId;
        }

        var message = $"{ex.GetType().Name}: {ex.Message}\nTraceId: {traceId ?? "unknown"}\nUserId: {userId ?? "anonymous"}\n{ex.StackTrace}";
        Enqueue("error", message, labels);
        return Task.CompletedTask;
    }

    public async Task FlushAsync()
    {
        if (string.IsNullOrWhiteSpace(_options.Url) || _queue.IsEmpty)
        {
            return;
        }

        var batch = new List<Entry>();
        while (batch.Count < MaxBatch && _queue.TryDequeue(out var entry))
        {
            batch.Add(entry);
        }
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            var streams = batch
                .GroupBy(BuildLabels, LabelComparer.Instance)
                .Select(group => new
                {
                    stream = group.Key,
                    values = group
                        .OrderBy(e => e.TimestampNs)
                        .Select(e => new[] { e.TimestampNs.ToString(), e.Message })
                        .ToArray(),
                })
                .ToArray();

            var payload = new { streams };

            using var req = new HttpRequestMessage(HttpMethod.Post, BuildPushUrl(_options.Url!));
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            ApplyAuth(req);

            var client = _httpClientFactory.CreateClient(nameof(LokiClient));
            using var response = await client.SendAsync(req);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            // Best-effort: drop the batch on failure rather than retrying
            // forever and risking unbounded memory growth.
        }
    }

    private Dictionary<string, string> BuildLabels(Entry entry)
    {
        var labels = new Dictionary<string, string>
        {
            ["app"] = _options.App ?? "dotnet-signing-server",
            ["level"] = entry.Level,
            ["env"] = ResolvedEnvironment,
        };
        if (entry.Labels != null)
        {
            foreach (var kv in entry.Labels)
            {
                labels[kv.Key] = kv.Value;
            }
        }
        return labels;
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(_options.BearerToken))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);
        }
        else if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            var bytes = Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
        }
        else if (!string.IsNullOrWhiteSpace(_options.Password))
        {
            var bytes = Encoding.UTF8.GetBytes($":{_options.Password}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
        }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        // Drain whatever is left so a clean shutdown doesn't lose the tail.
        FlushAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <summary>Equality over label sets so entries sharing labels land in one stream.</summary>
    private sealed class LabelComparer : IEqualityComparer<Dictionary<string, string>>
    {
        public static readonly LabelComparer Instance = new();

        public bool Equals(Dictionary<string, string>? x, Dictionary<string, string>? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null || x.Count != y.Count) return false;
            foreach (var kv in x)
            {
                if (!y.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
            }
            return true;
        }

        public int GetHashCode(Dictionary<string, string> obj)
        {
            var hash = new HashCode();
            foreach (var kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                hash.Add(kv.Key);
                hash.Add(kv.Value);
            }
            return hash.ToHashCode();
        }
    }
}
