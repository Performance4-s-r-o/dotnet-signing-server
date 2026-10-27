using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Consents;
using DotNetSigningServer.Services.Legal;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Services.Consents;

public class ConsentDocumentResolverTests
{
    private static string Meta(string document, int current) =>
        "{\"documents\":{\"" + document + "\":{\"requires_consent\":true,\"current_version\":" + current + ",\"required_version\":1}}}";

    private static async Task<IReadOnlyList<ConsentDocumentVersion>> ResolveAsync(string document, string docs = "Off", string? meta = null)
    {
        using var host = new OutboxTestHost(null, services =>
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new P4BackofficeProductOptions
            {
                Modules = { Docs = docs },
                BaseUrl = OutboxTestHost.BaseUrl,
                SecretKey = OutboxTestHost.SecretKey,
                Environment = "test",
                DisabledReason = BackofficeDisabledReason.None,
            })));
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (meta != null)
        {
            await BackofficeStateStore.SetAsync(db, BackofficeStateKeys.DocsMeta, meta, DateTimeOffset.UtcNow);
        }
        var resolver = new ConsentDocumentResolver(
            db, new Mock<ILegalDocumentSource>().Object, scope.ServiceProvider, NullLogger<ConsentDocumentResolver>.Instance);
        return await resolver.ResolveAsync([new ConsentRequirement(document, ConsentActions.Granted)], "en");
    }

    [Fact]
    public async Task DocumentWithAStaticPage_FallsBackToItsVersionOne()
    {
        Assert.Equal(1, Assert.Single(await ResolveAsync("terms")).Version);
    }

    [Fact]
    public async Task DocumentWithNoPageAndNoMeta_IsRefusedNotGivenAMadeUpVersion()
    {
        var ex = await Assert.ThrowsAsync<ConsentVersionUnknownException>(() => ResolveAsync("unknown-document"));
        Assert.Equal("unknown-document", ex.Document);
    }

    [Fact]
    public async Task DocsOn_MetaSaysNewer_ButNothingNewerCanBeShown_IsRefused()
    {
        await Assert.ThrowsAsync<ConsentVersionUnknownException>(() => ResolveAsync("terms", "On", Meta("terms", 3)));
    }

    [Fact]
    public async Task DocsRolledBackToOff_IgnoresMeta_AndNamesTheStaticPage()
    {
        Assert.Equal(1, Assert.Single(await ResolveAsync("terms", "Off", Meta("terms", 3))).Version);
    }

    [Fact]
    public async Task DocsOn_MetaVersionOne_IsTheStaticPage()
    {
        Assert.Equal(1, Assert.Single(await ResolveAsync("terms", "On", Meta("terms", 1))).Version);
    }

    [Fact]
    public async Task DocumentWithoutAPage_TakesTheMetaVersion()
    {
        Assert.Equal(4, Assert.Single(await ResolveAsync("custom_doc", "On", Meta("custom_doc", 4))).Version);
    }
}
