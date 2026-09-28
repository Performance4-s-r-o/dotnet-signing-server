using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotNetSigningServer.Services.Pricing;

/// <summary>One entry of <c>data.changes</c> of <c>price.scheduled</c> / <c>price.effective</c>.</summary>
/// <param name="Item">Price-list item key (<c>credits_300</c>); may be missing.</param>
/// <param name="From">Old amount in minor units; null for a new price.</param>
/// <param name="To">New amount in minor units; null for a tiered price.</param>
public sealed record PriceChangeEntry(string LookupKey, string? Item, string Currency, string? Interval, long? From, long? To);

/// <summary>The <c>data</c> of <c>price.scheduled</c> (and of the synthetic event of <see cref="PricingUpcomingCheck"/>).</summary>
public sealed record PriceChangeEvent(
    int Version,
    DateTimeOffset EffectiveFrom,
    int NoticeDays,
    IReadOnlyList<PriceChangeEntry> Changes,
    IReadOnlyList<string> RemovedLookupKeys)
{
    /// <summary>Null (with <paramref name="problem"/>) when <paramref name="data"/> lacks the version or the date.</summary>
    public static PriceChangeEvent? TryParse(JsonElement data, out string? problem)
    {
        problem = null;
        if (data.ValueKind != JsonValueKind.Object)
        {
            problem = "data is not an object";
            return null;
        }
        if (PriceEventData.Int(data, "version") is not { } version)
        {
            problem = "data.version is missing";
            return null;
        }
        if (PriceEventData.Date(data, "effective_from") is not { } effectiveFrom)
        {
            problem = "data.effective_from is missing";
            return null;
        }

        var changes = new List<PriceChangeEntry>();
        if (data.TryGetProperty("changes", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in list.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object) continue;
                var key = PriceEventData.String(c, "lookup_key");
                var currency = PriceEventData.String(c, "currency");
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(currency)) continue;
                changes.Add(new PriceChangeEntry(key, PriceEventData.String(c, "item"), currency,
                    PriceEventData.String(c, "interval"), PriceEventData.Long(c, "from"), PriceEventData.Long(c, "to")));
            }
        }

        var removed = new List<string>();
        if (data.TryGetProperty("removed_lookup_keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
        {
            removed.AddRange(keys.EnumerateArray()
                .Where(k => k.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(k.GetString()))
                .Select(k => k.GetString()!));
        }

        return new PriceChangeEvent(version, effectiveFrom, PriceEventData.Int(data, "notice_days") ?? 0, changes, removed);
    }
}

/// <summary>A credit pack whose price changes in a price-list version.</summary>
public sealed record PackPriceChange(int Quantity, string LookupKey, long OldMinor, long NewMinor, string Currency);

/// <summary>What the planner needs of a user.</summary>
public sealed record PriceNoticeCandidate(Guid UserId, bool AutoRechargeEnabled, int AutoRechargeQuantity, bool IsEnterprise, int? NotifiedVersion);

/// <summary>One <c>price_change_notice</c> to send.</summary>
public sealed record PriceNotice(Guid UserId, PackPriceChange Change);

/// <summary>Result of <see cref="PriceNoticePlanner.Plan"/>.</summary>
/// <param name="Changes">Changed packs in the product currency, by quantity.</param>
/// <param name="Notices">Users to notify (each once).</param>
/// <param name="AlreadyNotified">Affected users who already have the notice of this version.</param>
/// <param name="Problems">Changes that were skipped and why (for the log).</param>
public sealed record PriceNoticePlan(
    IReadOnlyDictionary<int, PackPriceChange> Changes,
    IReadOnlyList<PriceNotice> Notices,
    int AlreadyNotified,
    IReadOnlyList<string> Problems);

/// <summary>
/// Decides who gets a <c>price_change_notice</c> for a scheduled price-list version and with
/// which amounts. Pure; no I/O.
///
/// Only one-time prices of the credit packs the product sells (<c>credits_{n}</c>) in the product
/// currency count. A user is affected when auto-recharge is on for a pack whose price changes and
/// the account is not Enterprise. Idempotent per <c>(version, user)</c>: a user whose
/// <c>PriceChangeNotifiedVersion</c> is the version is not notified again.
/// </summary>
public static class PriceNoticePlanner
{
    private static readonly Regex CreditsItem = new(@"^credits_(\d+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <param name="currency">Product currency (<c>Stripe:Currency</c>).</param>
    /// <param name="currentPacks">Packs in force now: the old price when <c>from</c> is null, and
    /// the lookup key of a change without an item key.</param>
    public static PriceNoticePlan Plan(
        PriceChangeEvent evt,
        string currency,
        IReadOnlyList<CreditPack> currentPacks,
        IEnumerable<PriceNoticeCandidate> users)
    {
        var problems = new List<string>();
        var changes = CreditChanges(evt, currency, currentPacks, problems);

        var notices = new List<PriceNotice>();
        var already = 0;
        if (changes.Count > 0)
        {
            foreach (var user in users)
            {
                if (!IsAffected(user, changes)) continue;
                if (user.NotifiedVersion == evt.Version)
                {
                    already++;
                    continue;
                }
                notices.Add(new PriceNotice(user.UserId, changes[user.AutoRechargeQuantity]));
            }
        }
        return new PriceNoticePlan(changes, notices, already, problems);
    }

    /// <summary>Auto-recharge on (for a pack with a price change), not Enterprise.</summary>
    public static bool IsAffected(PriceNoticeCandidate user, IReadOnlyDictionary<int, PackPriceChange> changes) =>
        user.AutoRechargeEnabled
        && user.AutoRechargeQuantity > 0
        && !user.IsEnterprise
        && changes.ContainsKey(user.AutoRechargeQuantity);

    /// <summary>Price changes of sold credit packs in <paramref name="currency"/>, by quantity.</summary>
    public static IReadOnlyDictionary<int, PackPriceChange> CreditChanges(
        PriceChangeEvent evt, string currency, IReadOnlyList<CreditPack> currentPacks, List<string>? problems = null)
    {
        var result = new Dictionary<int, PackPriceChange>();
        var wanted = currency.Trim().ToUpperInvariant();
        foreach (var change in evt.Changes)
        {
            var quantity = QuantityOf(change, currentPacks);
            if (quantity is null || !CreditPricing.IsSoldQuantity(quantity.Value)) continue;
            if (!string.Equals(change.Currency.Trim(), wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (change.Interval != null && change.Interval != "one_time") continue;

            if (change.To is not { } to || to <= 0)
            {
                problems?.Add($"{change.LookupKey}: no single new amount");
                continue;
            }
            var from = change.From ?? currentPacks.FirstOrDefault(p => p.Quantity == quantity)?.UnitAmountMinor;
            if (from is null || from <= 0)
            {
                problems?.Add($"{change.LookupKey}: old amount unknown");
                continue;
            }
            if (from == to) continue;
            if (result.ContainsKey(quantity.Value))
            {
                problems?.Add($"{change.LookupKey}: second change of the {quantity} credits pack ignored");
                continue;
            }
            result[quantity.Value] = new PackPriceChange(quantity.Value, change.LookupKey, from.Value, to, wanted);
        }

        foreach (var key in evt.RemovedLookupKeys)
        {
            if (currentPacks.FirstOrDefault(p => p.LookupKey == key) is { } pack)
            {
                problems?.Add($"{key}: the {pack.Quantity} credits pack is removed from the price list");
            }
        }
        return result;
    }

    /// <summary>
    /// Whole days from <paramref name="now"/> until <paramref name="effectiveFrom"/>, rounded up;
    /// 0 when it is already in force.
    /// </summary>
    public static int DaysUntil(DateTimeOffset now, DateTimeOffset effectiveFrom) =>
        effectiveFrom <= now ? 0 : (int)Math.Ceiling((effectiveFrom - now).TotalDays);

    /// <summary>Minor units as the e-mail shows them (<c>14.25</c>, <c>5</c>).</summary>
    public static string FormatMinor(long minor) =>
        CreditPricing.FormatAmount(minor / (decimal)CreditPack.MinorUnitsPerMajor, CultureInfo.InvariantCulture);

    private static int? QuantityOf(PriceChangeEntry change, IReadOnlyList<CreditPack> currentPacks)
    {
        if (change.Item != null)
        {
            var match = CreditsItem.Match(change.Item);
            if (!match.Success) return null;
            return int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        }
        return currentPacks.FirstOrDefault(p => p.LookupKey == change.LookupKey)?.Quantity;
    }
}

/// <summary>Reading fields of an event's <c>data</c>; null for a missing field or a non-object.</summary>
internal static class PriceEventData
{
    public static JsonElement? Object(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.Object } v ? v : null;

    public static string? String(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static int? Int(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var n) ? n : null;

    public static long? Long(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : null;

    private static JsonElement? Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : null;

    public static DateTimeOffset? Date(JsonElement e, string name) =>
        String(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;
}
