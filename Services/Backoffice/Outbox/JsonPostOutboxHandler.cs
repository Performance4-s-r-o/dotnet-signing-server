using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DotNetSigningServer.Services.Backoffice.Outbox;

/// <summary>
/// Base for handlers that POST the stored payload as-is to one endpoint of the service and
/// read the created resource's <c>id</c> from the response.
/// </summary>
public abstract class JsonPostOutboxHandler : IOutboxHandler
{
    public abstract string Kind { get; }

    /// <summary>Endpoint relative to the service root, e.g. <c>v1/emails</c>.</summary>
    protected abstract string Path { get; }

    public virtual async Task<OutboxAttemptResult> SendAsync(OutboxRequest request, CancellationToken cancellationToken)
    {
        using var message = request.CreateHttpRequest(HttpMethod.Post, Path);
        message.Content = new StringContent(request.PayloadJson, Encoding.UTF8, "application/json");
        using var response = await request.Http.SendAsync(message, cancellationToken);
        return await ReadResultAsync(response, cancellationToken);
    }

    /// <summary>Field errors quoted in the log; enough to name the problem, short enough for <c>LastError</c>.</summary>
    public const int MaxReportedErrors = 3;

    /// <summary>Status, problem <c>code</c>, <c>id</c>, field <c>errors</c> and <c>Retry-After</c> of a service response.</summary>
    public static async Task<OutboxAttemptResult> ReadResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        string? code = null;
        string? id = null;
        string? details = null;

        var body = response.Content is null ? "" : await response.Content.ReadAsStringAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (status >= 400 && doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                        code = c.GetString();
                    if (status >= 400) details = ProblemDetails(doc.RootElement);
                    if (status < 300 && doc.RootElement.TryGetProperty("id", out var i))
                        id = i.ValueKind == JsonValueKind.String ? i.GetString() : i.GetRawText();
                }
            }
            catch (JsonException)
            {
                // Not JSON (a proxy's error page, …): the status alone decides.
            }
        }

        return OutboxAttemptResult.Response(status, code, id, RetryAfter(response.Headers.RetryAfter), details);
    }

    /// <summary>
    /// The <c>errors</c> of an RFC 9457 body as <c>path: message</c>, comma separated. Without
    /// them a refusal reads as a bare code (<c>HTTP 422 email_invalid</c>) and says nothing
    /// about which field the service refused.
    /// </summary>
    private static string? ProblemDetails(JsonElement problem)
    {
        if (!problem.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) return null;

        var parts = new List<string>();
        foreach (var error in errors.EnumerateArray())
        {
            if (parts.Count == MaxReportedErrors) break;
            if (error.ValueKind != JsonValueKind.Object) continue;
            var path = error.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (string.IsNullOrWhiteSpace(message)) continue;
            parts.Add(string.IsNullOrWhiteSpace(path) ? message! : $"{path}: {message}");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static TimeSpan? RetryAfter(RetryConditionHeaderValue? header)
    {
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }
}
