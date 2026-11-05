using System.Security.Claims;
using DotNetSigningServer.Controllers;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Pricing;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Services.Ai;

/// <summary>
/// AI price by pages: the base covers the first 5 pages the model gets, every further started 5
/// pages add the surcharge. The controller is called directly; the paid part is captured.
/// </summary>
public class AiPricingEndpointTests
{
    private sealed class Harness : PdfTemplateApiController
    {
        public readonly List<(string Operation, int BaseCost)> Calls = new();

        public Harness(ApplicationDbContext db, ContentLimitGuard limits, TemplateAiService ai, IStringLocalizer<SharedStrings> localizer)
            : base(db, new Mock<DotNetSigningServer.Services.IApiAuthService>().Object, NullLogger<PdfTemplateApiController>.Instance, limits,
                TestHelpers.WrapOptions(new BillingOptions()), TestHelpers.CreateMockEnvironment(),
                new PdfTemplateService(db, NullLogger<PdfTemplateService>.Instance, limits, new KeyEchoLocalizerFactory(), TestRuntimeSettings.Defaults),
                ai, localizer)
        {
        }

        protected override Task<IActionResult> RunOperationAsync(
            User user, string operation, string fingerprint, int baseCost,
            Func<CancellationToken, Task<PaidResult>> work, string? presignId = null, Func<Task>? afterCompleted = null)
        {
            Calls.Add((operation, baseCost));
            return Task.FromResult<IActionResult>(Ok());
        }
    }

    /// <summary>Page selection that fails in a way no guard foresaw.</summary>
    private sealed class BrokenPages() : TemplateAiService(null!, TestHelpers.WrapOptions(new LimitsOptions()), NullLogger<TemplateAiService>.Instance)
    {
        public override DotNetSigningServer.Services.Ai.AiDocument SelectPages(byte[] pdf, int? pageFrom, int? pageTo) =>
            throw new NullReferenceException("parser bug");
    }

    private static (Harness Controller, AiHost Host) Controller(IOperationPrices prices, bool brokenPages = false)
    {
        var limitOptions = new LimitsOptions { RejectActiveContent = true };
        var limits = new ContentLimitGuard(TestHelpers.WrapOptions(limitOptions));
        var db = TestHelpers.CreateInMemoryDbContext();
        var user = TestHelpers.CreateTestUser();
        user.CreditsRemaining = 1000;
        db.Users.Add(user);
        db.SaveChanges();
        var localizer = new Mock<IStringLocalizer<SharedStrings>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string k) => new LocalizedString(k, k));

        var host = new AiHost();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(prices)
            .AddSingleton(new PdfSafetyGuard(TestHelpers.WrapOptions(limitOptions)))
            .AddSingleton(TestHelpers.WrapOptions(limitOptions))
            .AddMvcCore().Services
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], ApiKeyAuthenticationHandler.SchemeName));
        var controller = new Harness(db, limits, brokenPages ? new BrokenPages() : host.Service, localizer.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        return (controller, host);
    }

    private static string Pdf(int pages) =>
        Convert.ToBase64String(AiPdfs.Pages(Enumerable.Range(1, pages).Select(i => AiPdfs.Text($"Page {i}")).ToArray()));

    private static AiExtractDataInput Extract(int pages, int? from = null, int? to = null) => new()
    {
        PdfContent = Pdf(pages),
        Columns = [new AiExtractColumnDefinition { Key = "a", Title = "A" }],
        PageFrom = from,
        PageTo = to,
    };

    private static readonly IOperationPrices Priced = TestRuntimeSettings.Prices(
        ("ai_extract", 2), ("ai_extract_pages", 1), ("ai_detect_fields", 3), ("ai_detect_fields_pages", 2));

    [Theory]
    [InlineData(1, 2)]
    [InlineData(5, 2)]
    [InlineData(6, 3)]
    [InlineData(10, 3)]
    [InlineData(11, 4)]
    [InlineData(20, 5)]
    [InlineData(400, 5)] // cut to 20 pages
    public async Task Extract_costs_the_base_plus_each_further_started_five_pages(int pages, int expected)
    {
        var (controller, host) = Controller(Priced);
        using var _ = host;
        await controller.ExtractData(Extract(pages));
        Assert.Equal(("ai-extract-data", expected), Assert.Single(controller.Calls));
    }

    [Fact]
    public async Task Only_the_pages_sent_count()
    {
        var (controller, host) = Controller(Priced);
        using var _ = host;
        await controller.ExtractData(Extract(30, from: 28)); // pages 28-30
        Assert.Equal(2, Assert.Single(controller.Calls).BaseCost);
    }

    [Fact]
    public async Task Detect_fields_uses_its_own_prices()
    {
        var (controller, host) = Controller(Priced);
        using var _ = host;
        await controller.DetectTemplateFields(new AiDetectFieldsInput { PdfContent = Pdf(12) });
        Assert.Equal(("ai-detect-fields", 3 + 2 * 2), Assert.Single(controller.Calls));
    }

    [Theory]
    [InlineData("ai_extract")]
    [InlineData("ai_extract_pages")]
    public async Task Without_either_price_AI_is_not_offered_even_for_a_short_document(string missing)
    {
        var costs = new[] { ("ai_extract", 2), ("ai_extract_pages", 1) }.Where(c => c.Item1 != missing).ToArray();
        var (controller, host) = Controller(TestRuntimeSettings.Prices(costs));
        using var _ = host;
        var result = await controller.ExtractData(Extract(1));
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, status.StatusCode);
        Assert.Contains(OperationNotOfferedException.Code, System.Text.Json.JsonSerializer.Serialize(status.Value));
        Assert.Empty(controller.Calls);
    }

    [Fact]
    public async Task Without_a_price_list_AI_is_not_offered()
    {
        var (controller, host) = Controller(TestRuntimeSettings.NoPriceList);
        using var _ = host;
        Assert.Equal(503, Assert.IsType<ObjectResult>(await controller.ExtractData(Extract(1))).StatusCode);
        Assert.Empty(controller.Calls);
    }

    [Fact]
    public async Task A_bad_page_range_is_400_before_any_charge()
    {
        var (controller, host) = Controller(Priced);
        using var _ = host;
        var result = await controller.ExtractData(Extract(3, from: 9));
        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Empty(controller.Calls);
    }

    [Fact]
    public async Task An_unforeseen_failure_reading_the_pages_is_a_localized_500_without_a_charge()
    {
        var (controller, host) = Controller(Priced, brokenPages: true);
        using var _ = host;
        var extract = Assert.IsType<ObjectResult>(await controller.ExtractData(Extract(3)));
        Assert.Equal(500, extract.StatusCode);
        Assert.Contains("AiExtractionFailed", System.Text.Json.JsonSerializer.Serialize(extract.Value));
        var detect = Assert.IsType<ObjectResult>(await controller.DetectTemplateFields(new AiDetectFieldsInput { PdfContent = Pdf(3) }));
        Assert.Equal(500, detect.StatusCode);
        Assert.Contains("AiDetectionFailed", System.Text.Json.JsonSerializer.Serialize(detect.Value));
        Assert.Empty(controller.Calls);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    [InlineData(6, 1)]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(20, 3)]
    public void Surcharge_units(int pages, int units) => Assert.Equal(units, PricedOperations.AiExtraPageUnits(pages));
}
