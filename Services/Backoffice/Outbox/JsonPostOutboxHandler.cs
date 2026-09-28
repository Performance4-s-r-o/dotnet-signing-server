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

    /// <summary>Status, problem <c>code</c>, <c>id</c> and <c>Retry-After</c> of a service response.</summary>
    public static async Task<OutboxAttemptResult> ReadResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        string? code = null;
        string? id = null;

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
                    if (status < 300 && doc.RootElement.TryGetProperty("id", out var i))
                        id = i.ValueKind == JsonValueKind.String ? i.GetString() : i.GetRawText();
                }
            }
            catch (JsonException)
            {
                // Not JSON (a proxy's error page, …): the status alone decides.
            }
        }

        return OutboxAttemptResult.Response(status, code, id, RetryAfter(response.Headers.RetryAfter));
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
