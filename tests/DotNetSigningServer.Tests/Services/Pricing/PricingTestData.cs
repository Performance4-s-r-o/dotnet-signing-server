using System.Text.Json;
using DotNetSigningServer.Services.Pricing;

namespace DotNetSigningServer.Tests.Services.Pricing;

/// <summary>Bodies of <c>GET /v1/pricing/current</c> shaped like the service's <c>PriceBookVersion</c>.</summary>
internal static class PricingTestData
{
    /// <summary>The imported price list: credits_100/300/500/1000 at 500/1425/2250/4250 cents EUR.</summary>
    public static readonly (int Quantity, long Cents)[] Imported = [(100, 500), (300, 1425), (500, 2250), (1000, 4250)];

    public static Dictionary<string, object?> Item(int quantity, long cents, string currency = "eur",
        string interval = "one_time", object? quantityValue = null, string? tiersMode = null, string kind = "credits") => new()
        {
            ["key"] = $"credits_{quantity}",
            ["kind"] = kind,
            ["name"] = $"{quantity} credits",
            ["names"] = new Dictionary<string, string> { ["en"] = $"{quantity} credits" },
            ["description"] = "",
            ["attributes"] = new Dictionary<string, object?> { ["quantity"] = quantityValue ?? quantity },
            ["entitlements"] = new Dictionary<string, object?>(),
            ["prices"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["lookup_key"] = $"pd_credits_{quantity}_once_{currency}",
                    ["currency"] = currency,
                    ["unit_amount"] = cents,
                    ["interval"] = interval,
                    ["tiers_mode"] = tiersMode,
                    ["tiers"] = null,
                    ["tax_behavior"] = "unspecified",
                    ["currency_options"] = null,
                    ["stripe_price_id"] = $"price_{quantity}",
                },
            },
        };

    public static string Json(int version = 1, params Dictionary<string, object?>[] items) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = version,
            ["label"] = "v" + version,
            ["status"] = "effective",
            ["effective_from"] = "2026-09-28T00:00:00Z",
            ["published_at"] = "2026-09-28T00:00:00Z",
            ["notice_days"] = 30,
            ["sync_status"] = "synced",
            ["summary"] = null,
            ["locale"] = "en",
            ["items"] = items,
        });

    /// <summary>The imported price list, optionally with other amounts.</summary>
    public static string ImportedJson(int version = 1, IReadOnlyDictionary<int, long>? amounts = null) =>
        Json(version, Imported.Select(p => Item(p.Quantity, amounts?.GetValueOrDefault(p.Quantity, p.Cents) ?? p.Cents)).ToArray());

    public static PricingSnapshot Snapshot(string json, string? etag = "\"v1\"") =>
        PricingSnapshot.FromResponse(json, etag, new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));

    public static CreditPack Pack(int quantity = 300, long cents = 1425, string? lookupKey = "pd_credits_300_once_eur") =>
        new(quantity, cents, "EUR", lookupKey, "price_" + quantity);
}
