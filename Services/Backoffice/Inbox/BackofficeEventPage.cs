using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetSigningServer.Services.Backoffice.Inbox;

/// <summary>
/// Response of <c>GET /v1/events</c> (schema <c>EventPage</c> of the service's OpenAPI).
/// Hand-written because the default build leaves the SDK out; with the SDK in every build
/// this becomes <c>BackofficeApiClient.V1.Events.GetAsync</c>.
/// </summary>
public sealed class BackofficeEventPage
{
    [JsonPropertyName("data")]
    public List<BackofficeEventDto> Data { get; set; } = new();

    /// <summary>Store it and send it as <c>since</c> next time, also after an empty page.</summary>
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    /// <summary>Another page is ready right now.</summary>
    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }

    /// <summary>The requested position was older than the 30-day window; events may have been missed.</summary>
    [JsonPropertyName("window_clamped")]
    public bool WindowClamped { get; set; }
}

/// <summary>Schema <c>Event</c>: <see cref="Id"/> equals the <c>webhook-id</c> of the delivery.</summary>
public sealed class BackofficeEventDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; set; }

    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }
}
