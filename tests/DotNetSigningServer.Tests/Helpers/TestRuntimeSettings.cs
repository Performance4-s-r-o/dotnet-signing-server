using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Helpers;

/// <summary>Runtime settings and operation prices for tests that build services by hand.</summary>
public static class TestRuntimeSettings
{
    /// <summary>Defaults in code, AI closed (no settings service).</summary>
    public static RuntimeSettings Defaults => With();

    /// <summary>Env overrides by env name (<c>MAX_TEMPLATES_PER_USER</c> = <c>"3"</c>).</summary>
    public static RuntimeSettings With(params (string Env, string Value)[] env) =>
        new(RuntimeSettingsOverrides.FromValues(env.ToDictionary(e => e.Env, e => (string?)e.Value)),
            TimeProvider.System, NullLogger<RuntimeSettings>.Instance);

    /// <summary>Settings as read just now from a service that switched AI on.</summary>
    public static RuntimeSettings AiOn()
    {
        var settings = Defaults;
        settings.Apply(RuntimeSettingsSnapshot.Parse(
            """{"version":1,"items":[{"key":"ai_enabled","type":"bool","value":true,"default":false,"fail_mode":"closed"}]}"""),
            TimeProvider.System.GetUtcNow());
        return settings;
    }

    /// <summary>No price list: every price is missing (1 credit, AI not offered).</summary>
    public static IOperationPrices NoPriceList { get; } = new OperationPrices(null, NullLogger<OperationPrices>.Instance);

    /// <summary>Prices from a price list with these <c>credit_costs</c>.</summary>
    public static IOperationPrices Prices(params (string Key, int Credits)[] costs)
    {
        var holder = new PricingSnapshotHolder();
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1,
            items = Array.Empty<object>(),
            credit_costs = costs.ToDictionary(c => c.Key, c => c.Credits),
        });
        holder.Set(PricingSnapshot.FromResponse(body, null, DateTimeOffset.UtcNow));
        return new OperationPrices(holder, NullLogger<OperationPrices>.Instance);
    }
}
