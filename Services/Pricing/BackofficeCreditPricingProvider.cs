using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services.Pricing;

/// <summary>
/// Prices from the service's price list (<c>Modules:Pricing=On</c>): the snapshot of
/// <c>GET /v1/pricing/current</c> held in memory by <see cref="PricingSnapshotHolder"/>, mapped
/// by <see cref="PriceListMapper"/> in <c>Stripe:Currency</c>. Never calls the service.
///
/// Without a snapshot every pack comes from <see cref="ConfigCreditPricingProvider"/>; a pack
/// missing from the snapshot (or priced in another currency) falls back to its configured
/// price alone. Both are logged as warnings, once per snapshot.
/// </summary>
public sealed class BackofficeCreditPricingProvider : ICreditPricingProvider
{
    private readonly PricingSnapshotHolder _holder;
    private readonly ConfigCreditPricingProvider _config;
    private readonly IOptionsMonitor<StripeOptions> _stripe;
    private readonly ILogger<BackofficeCreditPricingProvider> _logger;
    private readonly object _lock = new();
    private Resolved? _resolved;
    private bool _warnedNoSnapshot;

    public BackofficeCreditPricingProvider(
        PricingSnapshotHolder holder,
        ConfigCreditPricingProvider config,
        IOptionsMonitor<StripeOptions> stripe,
        ILogger<BackofficeCreditPricingProvider> logger)
    {
        _holder = holder;
        _config = config;
        _stripe = stripe;
        _logger = logger;
    }

    public string Source => Current().FromSnapshot ? CreditPricing.SourceBackoffice : CreditPricing.SourceConfig;

    public IReadOnlyList<CreditPack> GetPacks() => Current().Packs;

    public CreditPack? GetPack(int quantity) => Current().Packs.FirstOrDefault(p => p.Quantity == quantity);

    public decimal PricePer100 => GetPack(100)?.Amount ?? _config.PricePer100;

    public string Currency => Current().Currency;

    private sealed record Resolved(PricingSnapshot? Snapshot, string CurrencyKey, IReadOnlyList<CreditPack> Packs, string Currency, bool FromSnapshot);

    private Resolved Current()
    {
        var snapshot = _holder.Current;
        var currency = ConfigCreditPricingProvider.NormalizeCurrency(_stripe.CurrentValue.Currency);
        var resolved = _resolved;
        if (resolved != null && ReferenceEquals(resolved.Snapshot, snapshot) && resolved.CurrencyKey == currency)
        {
            return resolved;
        }

        lock (_lock)
        {
            resolved = _resolved;
            if (resolved != null && ReferenceEquals(resolved.Snapshot, snapshot) && resolved.CurrencyKey == currency)
            {
                return resolved;
            }
            _resolved = resolved = Resolve(snapshot, currency);
            return resolved;
        }
    }

    private Resolved Resolve(PricingSnapshot? snapshot, string currency)
    {
        if (snapshot is null)
        {
            if (!_warnedNoSnapshot)
            {
                _warnedNoSnapshot = true;
                _logger.LogWarning("[pricing] no price-list snapshot yet; using the configured prices (Billing:*)");
            }
            return new Resolved(null, currency, _config.GetPacks(), _config.Currency, FromSnapshot: false);
        }

        var mapping = PriceListMapper.Map(snapshot.Body, currency);
        var packs = new List<CreditPack>();
        foreach (var quantity in CreditPricing.Quantities)
        {
            if (mapping.Packs.TryGetValue(quantity, out var pack))
            {
                packs.Add(pack);
            }
            else if (_config.GetPack(quantity) is { } fallback)
            {
                packs.Add(fallback);
            }
        }

        if (mapping.Problems.Count > 0)
        {
            _logger.LogWarning(
                "[pricing] price list v{Version} is incomplete for {Currency} ({Problems}); those packs use the configured prices",
                snapshot.Version, currency, string.Join("; ", mapping.Problems));
        }
        else
        {
            _logger.LogInformation("[pricing] using price list v{Version} ({Currency})", snapshot.Version, currency);
        }

        // With nothing usable in the snapshot the prices are the configured ones.
        var fromSnapshot = mapping.Packs.Count > 0;
        return new Resolved(snapshot, currency, packs, fromSnapshot ? currency : _config.Currency, fromSnapshot);
    }
}
