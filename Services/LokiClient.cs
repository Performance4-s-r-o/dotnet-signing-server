using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services;

public class LokiClient
{
    private readonly HttpClient _httpClient;
    private readonly LokiOptions _options;

    public LokiClient(HttpClient httpClient, IOptions<LokiOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task LogAsync(string level, string message, IDictionary<string, string>? labels = null)
    {
        if (string.IsNullOrWhiteSpace(_options.Url))
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000; // nanoseconds
            var environment = _options.Environment
                              ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                              ?? "production";
            var streamLabels = new Dictionary<string, string>
            {
                ["app"] = _options.App ?? "dotnet-signing-server",
                ["level"] = level,
                ["env"] = environment
            };

            if (labels != null)
            {
                foreach (var kv in labels)
                {
                    streamLabels[kv.Key] = kv.Value;
                }
            }

            var payload = new
            {
                streams = new[]
                {
                    new
                    {
                        stream = streamLabels,
                        values = new[]
                        {
                            new[] { now.ToString(), message }
                        }
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, _options.Url!.TrimEnd('/') + "/loki/api/v1/push");
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            if (!string.IsNullOrWhiteSpace(_options.BearerToken))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);
            }
            else if (!string.IsNullOrWhiteSpace(_options.Username))
            {
                var bytes = Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
            }

            using var response = await _httpClient.SendAsync(req);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            // Best-effort: ignore logging failures
        }
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
        return LogAsync("error", message, labels);
    }
}
