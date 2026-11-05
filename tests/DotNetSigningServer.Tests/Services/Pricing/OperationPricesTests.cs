using System.Text.Json;
using System.Text.RegularExpressions;
using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Tests.Helpers;

namespace DotNetSigningServer.Tests.Services.Pricing;

/// <summary>Credits per operation from the price list's <c>credit_costs</c> (p4-backoffice operation costs).</summary>
public class OperationPricesTests
{
    [Fact]
    public void A_price_from_the_price_list_is_used_and_zero_is_free()
    {
        var prices = TestRuntimeSettings.Prices(("sign", 3), ("flatten_form", 0), ("ai_extract", 5));
        Assert.Equal(3, prices.Credits(PricedOperations.Sign));
        Assert.Equal(0, prices.Credits(PricedOperations.FlattenForm));
        Assert.Equal(5, prices.Credits(PricedOperations.AiExtract));
        Assert.Equal(12, prices.Credits(PricedOperations.Sign, 4));
    }

    [Fact]
    public void A_missing_price_is_one_credit_but_an_operation_requiring_a_price_is_not_offered()
    {
        var prices = TestRuntimeSettings.Prices(("sign", 3));
        Assert.Equal(1, prices.Credits(PricedOperations.Timestamp));
        Assert.False(prices.IsOffered(PricedOperations.AiExtract));
        Assert.False(prices.IsOffered(PricedOperations.AiDetectFields));
        var ex = Assert.Throws<OperationNotOfferedException>(() => prices.Credits(PricedOperations.AiExtract));
        Assert.Equal("ai_extract", ex.Key);
    }

    [Fact]
    public void Without_a_price_list_every_price_is_missing()
    {
        var prices = TestRuntimeSettings.NoPriceList;
        foreach (var operation in PricedOperations.Manifest)
        {
            Assert.Equal(operation.RequiresPrice ? null : 1, prices.PerUnit(operation.Key));
        }
    }

    [Fact]
    public void A_negative_or_text_price_counts_as_missing()
    {
        var holder = new PricingSnapshotHolder();
        holder.Set(PricingSnapshot.FromResponse(
            """{"version":2,"items":[],"credit_costs":{"sign":-1,"ai_extract":"2"}}""", null, DateTimeOffset.UtcNow));
        var prices = new OperationPrices(holder, Microsoft.Extensions.Logging.Abstractions.NullLogger<OperationPrices>.Instance);
        Assert.Equal(1, prices.PerUnit("sign"));
        Assert.Null(prices.PerUnit("ai_extract"));
    }

    [Fact]
    public void A_new_snapshot_is_picked_up()
    {
        var holder = new PricingSnapshotHolder();
        var prices = new OperationPrices(holder, Microsoft.Extensions.Logging.Abstractions.NullLogger<OperationPrices>.Instance);
        Assert.Null(prices.PerUnit("ai_extract"));
        holder.Set(PricingSnapshot.FromResponse("""{"version":3,"items":[],"credit_costs":{"ai_extract":2}}""", null, DateTimeOffset.UtcNow));
        Assert.Equal(2, prices.PerUnit("ai_extract"));
    }

    [Fact]
    public void The_dotnet_catalog_keys_are_in_the_manifest()
    {
        // The p4-dotnet table of the operation-costs contract.
        string[] catalog =
        [
            "sign", "sign_pfx", "timestamp", "visual_sign", "extend_signature", "attachment",
            "pdfa_convert", "flatten_form", "fill_pdf", "find_codes", "ai_detect_fields", "ai_extract",
        ];
        Assert.Equal(catalog.Order(), PricedOperations.Manifest.Select(o => o.Key).Order());
        Assert.True(PricedOperations.Get("ai_extract").RequiresPrice);
        Assert.True(PricedOperations.Get("ai_detect_fields").RequiresPrice);
        Assert.All(PricedOperations.Manifest.Where(o => !o.Key.StartsWith("ai_")), o => Assert.False(o.RequiresPrice));
        Assert.All(PricedOperations.Manifest, o => Assert.Matches("^[a-z][a-z0-9_]{1,39}$", o.Key));
    }

    /// <summary>Every operation the controllers charge through RunOperationAsync is in the manifest.</summary>
    [Fact]
    public void Every_charged_operation_in_the_code_is_in_the_manifest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Controllers"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var charged = new HashSet<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(dir!.FullName, "Controllers"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"Run(?:Operation|Ai)Async\(\s*user,\s*""([a-z-]+)"""))
            {
                charged.Add(m.Groups[1].Value);
            }
        }
        Assert.NotEmpty(charged);
        foreach (var operation in charged)
        {
            Assert.True(PricedOperations.ByApiOperation.TryGetValue(operation, out var key), $"{operation} is charged but not mapped to a price key");
            PricedOperations.Get(key!);
        }
        Assert.Equal(charged.Order(), PricedOperations.ByApiOperation.Keys.Order());
    }

    [Fact]
    public void The_manifest_body_has_the_contract_shape()
    {
        using var document = JsonDocument.Parse(OperationsManifestPublisher.Body("1.2.3"));
        var root = document.RootElement;
        Assert.Equal("1.2.3", root.GetProperty("code_version").GetString());
        var first = root.GetProperty("operations")[0];
        foreach (var field in new[] { "key", "label", "description", "unit", "default_credits", "requires_price" })
        {
            Assert.True(first.TryGetProperty(field, out _), field);
        }
        Assert.Equal(PricedOperations.Manifest.Count, root.GetProperty("operations").GetArrayLength());
    }
}
