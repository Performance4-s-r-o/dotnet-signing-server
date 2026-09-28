using System.Net;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Tests.Services.Consents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Tests.Services.Pricing;

/// <summary><c>/pricing</c> rendered by the whole app (throwaway PostgreSQL; the service is never called).</summary>
[Trait("Category", "Db")]
public class PricingPageTests : IClassFixture<SignUpPostgresFixture>
{
    private readonly SignUpPostgresFixture _fixture;

    public PricingPageTests(SignUpPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<string> PricingHtmlAsync(Action<IServiceCollection>? configure = null)
    {
        await using var factory = new SignUpAppFactory(_fixture.Postgres.ConnectionString, "Off", configure);
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/en/pricing");
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently)
        {
            response = await client.GetAsync("/pricing");
        }
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Off_ShowsTheConfiguredPrice()
    {
        var html = await PricingHtmlAsync();

        Assert.Contains("5 EUR", html);
        Assert.Contains("0 EUR", html);
    }

    [Fact]
    public async Task PriceList_ShowsTheServicePriceWithoutCallingTheService()
    {
        var html = await PricingHtmlAsync(services =>
        {
            var holder = new PricingSnapshotHolder();
            holder.Set(PricingTestData.Snapshot(PricingTestData.ImportedJson(2, new Dictionary<int, long> { [100] = 600, [300] = 1710, [500] = 2700, [1000] = 5100 })));
            services.RemoveAll<ICreditPricingProvider>();
            services.AddSingleton<ICreditPricingProvider>(sp => new BackofficeCreditPricingProvider(
                holder,
                new ConfigCreditPricingProvider(new BillingOptions()),
                sp.GetRequiredService<IOptionsMonitor<StripeOptions>>(),
                NullLogger<BackofficeCreditPricingProvider>.Instance));
        });

        Assert.Contains("6 EUR", html);
        Assert.DoesNotContain("5 EUR", html);
    }
}
