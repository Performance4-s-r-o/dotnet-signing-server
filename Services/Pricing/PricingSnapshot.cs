using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetSigningServer.Services.Pricing;

/// <summary>
/// The price list in force as last fetched from <c>GET /v1/pricing/current</c>, kept verbatim
/// (<see cref="Body"/>) so the stored copy maps exactly like a fresh response.
/// Stored as JSON in <c>BackofficeState["pricing:current"]</c>.
/// </summary>
public sealed class PricingSnapshot
{
    /// <summary>Price-list version (<c>version</c> of the body); 0 when missing.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; }

    /// <summary>ETag of the response, sent back as <c>If-None-Match</c>.</summary>
    [JsonPropertyName("etag")]
    public string? ETag { get; init; }

    [JsonPropertyName("fetched_at")]
    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>The response body (<c>PriceBookVersion</c>).</summary>
    [JsonPropertyName("body")]
    public JsonElement Body { get; init; }

    private static readonly JsonSerializerOptions Json = new();

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>Null when <paramref name="json"/> is empty or not a stored snapshot.</summary>
    public static PricingSnapshot? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<PricingSnapshot>(json, Json);
            return snapshot is { Body.ValueKind: JsonValueKind.Object } ? snapshot : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A snapshot of a response body; throws <see cref="JsonException"/> when it is not a price list.</summary>
    public static PricingSnapshot FromResponse(string body, string? etag, DateTimeOffset fetchedAt)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Price list response has no items array");
        }
        var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
        return new PricingSnapshot { Version = version, ETag = etag, FetchedAt = fetchedAt, Body = root.Clone() };
    }
}

/// <summary>A pack found in a price list, or why a sold pack was not.</summary>
public sealed record PriceListMapping(IReadOnlyDictionary<int, CreditPack> Packs, IReadOnlyList<string> Problems);

/// <summary>
/// Maps a price list (<c>PriceBookVersion</c>) to credit packs: items of kind <c>credits</c>
/// with an integer <c>attributes.quantity</c>, and of each such item the one-time, non-tiered
/// price in the wanted currency. Items for quantities the product does not sell are ignored.
/// Pure; no I/O.
/// </summary>
public static class PriceListMapper
{
    public static PriceListMapping Map(JsonElement body, string currency)
    {
        var packs = new Dictionary<int, CreditPack>();
        var problems = new List<string>();
        var wanted = currency.Trim();

        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            problems.Add("the price list has no items");
            return new PriceListMapping(packs, problems);
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || String(item, "kind") != "credits") continue;
            var key = String(item, "key") ?? "?";
            var quantity = Quantity(item);
            if (quantity is null)
            {
                problems.Add($"item {key} has no integer attributes.quantity");
                continue;
            }
            if (!CreditPricing.IsSoldQuantity(quantity.Value) || packs.ContainsKey(quantity.Value)) continue;

            var pack = PackFrom(item, quantity.Value, wanted);
            if (pack is null)
            {
                problems.Add($"item {key} has no one-time price in {wanted.ToUpperInvariant()}");
                continue;
            }
            packs[quantity.Value] = pack;
        }

        foreach (var quantity in CreditPricing.Quantities.Where(q => !packs.ContainsKey(q)))
        {
            problems.Add($"no credits item for {quantity} credits in {wanted.ToUpperInvariant()}");
        }
        return new PriceListMapping(packs, problems);
    }

    private static CreditPack? PackFrom(JsonElement item, int quantity, string currency)
    {
        if (!item.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Array) return null;
        foreach (var price in prices.EnumerateArray())
        {
            if (price.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(String(price, "currency"), currency, StringComparison.OrdinalIgnoreCase)) continue;
            var interval = String(price, "interval");
            if (interval != null && interval != "one_time") continue;
            if (price.TryGetProperty("tiers_mode", out var tiers) && tiers.ValueKind == JsonValueKind.String) continue;
            if (!price.TryGetProperty("unit_amount", out var amount)
                || amount.ValueKind != JsonValueKind.Number
                || !amount.TryGetInt64(out var minor)
                || minor <= 0)
            {
                continue;
            }

            return new CreditPack(
                quantity,
                minor,
                ConfigCreditPricingProvider.NormalizeCurrency(currency),
                NullIfEmpty(String(price, "lookup_key")),
                NullIfEmpty(String(price, "stripe_price_id")));
        }
        return null;
    }

    /// <summary><c>attributes.quantity</c> as a number or a numeric string.</summary>
    private static int? Quantity(JsonElement item)
    {
        if (!item.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object
            || !attributes.TryGetProperty("quantity", out var q))
        {
            return null;
        }
        return q.ValueKind switch
        {
            JsonValueKind.Number when q.TryGetInt32(out var n) && n > 0 => n,
            JsonValueKind.String when int.TryParse(q.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s > 0 => s,
            _ => null,
        };
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The snapshot currently in memory (singleton). Filled from the database at startup and by
/// every successful refresh; read by <see cref="BackofficeCreditPricingProvider"/> on request paths.
/// </summary>
public sealed class PricingSnapshotHolder
{
    private volatile PricingSnapshot? _current;

    public PricingSnapshot? Current => _current;

    /// <summary>Raised after <see cref="Set"/>; listeners must not throw.</summary>
    public event Action<PricingSnapshot>? Changed;

    public void Set(PricingSnapshot snapshot)
    {
        _current = snapshot;
        Changed?.Invoke(snapshot);
    }
}
