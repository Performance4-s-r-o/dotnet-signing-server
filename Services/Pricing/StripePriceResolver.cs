using System.Collections.Concurrent;
using Stripe;

namespace DotNetSigningServer.Services.Pricing;

/// <summary>
/// Finds the Stripe Price of a credit pack by its lookup key
/// (<c>GET /v1/prices?lookup_keys[]=…&amp;active=true</c>) for Checkout.
///
/// Answers are cached for <see cref="CacheDuration"/> (including "not found"), and dropped by
/// <see cref="Invalidate"/> after <c>price.effective</c> or a new price-list version. The price
/// is used only when it is active, one-time, not tiered, and its amount and currency match the
/// pack (the snapshot); otherwise the result is null and Checkout sends the amount inline
/// (<c>price_data</c>). Stripe failures also give null. Never throws.
/// </summary>
public sealed class StripePriceResolver
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private readonly IStripeClient? _client;
    private readonly TimeProvider _time;
    private readonly ILogger<StripePriceResolver> _logger;
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private long _generation;

    /// <param name="client">Null when Stripe is not configured: every lookup is then null.</param>
    public StripePriceResolver(IStripeClient? client, TimeProvider time, ILogger<StripePriceResolver> logger)
    {
        _client = client;
        _time = time;
        _logger = logger;
    }

    /// <summary>What Stripe answered for a lookup key; <see cref="Price"/> null = no active price.</summary>
    private sealed record Entry(Price? Price, DateTimeOffset ExpiresAt, long Generation);

    /// <summary>Drops every cached answer.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _cache.Clear();
    }

    /// <summary>The Stripe Price id to charge <paramref name="pack"/> with, or null to send the amount inline.</summary>
    public async Task<string?> ResolveAsync(CreditPack pack, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pack.LookupKey) || _client is null)
        {
            return null;
        }

        var key = pack.LookupKey;
        var now = _time.GetUtcNow();
        if (!_cache.TryGetValue(key, out var entry) || entry.ExpiresAt <= now)
        {
            var generation = Interlocked.Read(ref _generation);
            Price? price;
            try
            {
                price = await LookupAsync(key, cancellationToken);
            }
            catch (Exception ex) when (ex is StripeException or HttpRequestException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // Not cached: the next checkout asks again.
                _logger.LogWarning(ex, "[pricing] Stripe Price lookup for {LookupKey} failed; charging {Quantity} credits inline", key, pack.Quantity);
                return null;
            }

            entry = new Entry(price, now + CacheDuration, generation);
            // An invalidation during the lookup wins: that answer may already be outdated.
            if (Interlocked.Read(ref _generation) == generation)
            {
                _cache[key] = entry;
            }
        }

        return Check(pack, entry.Price);
    }

    private async Task<Price?> LookupAsync(string lookupKey, CancellationToken cancellationToken)
    {
        var service = new PriceService(_client);
        var list = await service.ListAsync(new PriceListOptions
        {
            LookupKeys = [lookupKey],
            Active = true,
            Limit = 1,
        }, cancellationToken: cancellationToken);
        return list?.Data?.FirstOrDefault(p => p.LookupKey == lookupKey);
    }

    private string? Check(CreditPack pack, Price? price)
    {
        if (price is null)
        {
            _logger.LogWarning("[pricing] no active Stripe Price for {LookupKey}; charging {Quantity} credits inline", pack.LookupKey, pack.Quantity);
            return null;
        }

        string? problem = null;
        if (!price.Active) problem = "it is not active";
        else if (price.Type is not null && price.Type != "one_time") problem = $"it is {price.Type}";
        else if (price.Tiers is { Count: > 0 } || price.TiersMode is not null) problem = "it is tiered";
        else if (!string.Equals(price.Currency, pack.Currency, StringComparison.OrdinalIgnoreCase)) problem = $"its currency is {price.Currency}";
        else if (price.UnitAmount != pack.UnitAmountMinor) problem = $"its amount is {price.UnitAmount}, the price list says {pack.UnitAmountMinor}";

        if (problem != null)
        {
            _logger.LogWarning("[pricing] Stripe Price {PriceId} ({LookupKey}) not used because {Problem}; charging {Quantity} credits inline",
                price.Id, pack.LookupKey, problem, pack.Quantity);
            return null;
        }

        if (pack.StripePriceId != null && pack.StripePriceId != price.Id)
        {
            _logger.LogInformation("[pricing] {LookupKey} now points to {PriceId} (price list: {ListedPriceId})",
                pack.LookupKey, price.Id, pack.StripePriceId);
        }
        return price.Id;
    }
}
