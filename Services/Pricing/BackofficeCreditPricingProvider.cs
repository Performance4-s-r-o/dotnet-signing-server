namespace DotNetSigningServer.Services.Pricing;

/// <summary>
/// Prices from the service's price list (<c>Modules:Pricing=On</c>): the snapshot of
/// <c>GET /v1/pricing/current</c> held in memory by <see cref="PricingSnapshotHolder"/>, mapped
/// by <see cref="PriceListMapper"/> in the price list's own currency. Never calls the service.
///
/// Without a snapshot nothing is sold (no fallback prices, #175); a pack missing from the
/// snapshot is not sold. Both are logged as warnings, once per snapshot.
/// </summary>
public sealed class BackofficeCreditPricingProvider : ICreditPricingProvider
{
    private readonly PricingSnapshotHolder _holder;
    private readonly ILogger<BackofficeCreditPricingProvider> _logger;
    private readonly object _lock = new();
    private Resolved? _resolved;
    private bool _warnedNoSnapshot;

    public BackofficeCreditPricingProvider(PricingSnapshotHolder holder, ILogger<BackofficeCreditPricingProvider> logger)
    {
        _holder = holder;
        _logger = logger;
    }

    public IReadOnlyList<CreditPack> GetPacks() => Current().Packs;

    public CreditPack? GetPack(int quantity) => Current().Packs.FirstOrDefault(p => p.Quantity == quantity);

    public decimal? PricePer100 => GetPack(100)?.Amount;

    public string? Currency => Current().Currency;

    private sealed record Resolved(PricingSnapshot? Snapshot, IReadOnlyList<CreditPack> Packs, string? Currency);

    private Resolved Current()
    {
        var snapshot = _holder.Current;
        var resolved = _resolved;
        if (resolved != null && ReferenceEquals(resolved.Snapshot, snapshot))
        {
            return resolved;
        }

        lock (_lock)
        {
            resolved = _resolved;
            if (resolved != null && ReferenceEquals(resolved.Snapshot, snapshot))
            {
                return resolved;
            }
            _resolved = resolved = Resolve(snapshot);
            return resolved;
        }
    }

    private Resolved Resolve(PricingSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            if (!_warnedNoSnapshot)
            {
                _warnedNoSnapshot = true;
                _logger.LogWarning("[pricing] no price-list snapshot yet; nothing is sold until one arrives");
            }
            return new Resolved(null, [], null);
        }

        var mapping = PriceListMapper.Map(snapshot.Body);
        var packs = CreditPricing.Quantities
            .Where(mapping.Packs.ContainsKey)
            .Select(q => mapping.Packs[q])
            .ToList();

        if (mapping.Problems.Count > 0)
        {
            _logger.LogWarning(
                "[pricing] price list v{Version} is incomplete ({Problems}); those packs are not sold",
                snapshot.Version, string.Join("; ", mapping.Problems));
        }
        else
        {
            _logger.LogInformation("[pricing] using price list v{Version} ({Currency})", snapshot.Version, mapping.Currency);
        }

        return new Resolved(snapshot, packs, packs.Count > 0 ? mapping.Currency : null);
    }
}
