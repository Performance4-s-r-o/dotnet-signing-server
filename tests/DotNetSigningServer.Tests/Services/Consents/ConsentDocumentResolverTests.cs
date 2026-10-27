using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Consents;
using DotNetSigningServer.Services.Legal;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Services.Consents;

public class ConsentDocumentResolverTests
{
    private static async Task<IReadOnlyList<ConsentDocumentVersion>> ResolveAsync(string document)
    {
        using var host = ConsentTestHost.Create();
        using var scope = host.Services.CreateScope();
        var source = new Mock<ILegalDocumentSource>();
        var resolver = new ConsentDocumentResolver(
            scope.ServiceProvider.GetRequiredService<DotNetSigningServer.Data.ApplicationDbContext>(),
            source.Object, scope.ServiceProvider, NullLogger<ConsentDocumentResolver>.Instance);
        return await resolver.ResolveAsync([new ConsentRequirement(document, ConsentActions.Granted)], "en");
    }

    [Fact]
    public async Task DocumentWithAStaticPage_FallsBackToItsVersionOne()
    {
        var doc = Assert.Single(await ResolveAsync("terms"));
        Assert.Equal(ConsentDocumentResolver.StaticPageVersion, doc.Version);
    }

    [Fact]
    public async Task DocumentWithNoPageAndNoMeta_IsRefusedNotGivenAMadeUpVersion()
    {
        var ex = await Assert.ThrowsAsync<ConsentVersionUnknownException>(() => ResolveAsync("unknown-document"));
        Assert.Equal("unknown-document", ex.Document);
    }
}
