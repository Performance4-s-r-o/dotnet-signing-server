using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Consents;
using DotNetSigningServer.Services.Legal;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Services.Consents;

/// <summary>
/// The version a consent form records and the version the re-consent gate requires come from the
/// same rule (docs:meta only with Docs=On); otherwise a user consents v1 and is asked for v3 forever.
/// </summary>
public class ConsentDocsModeConsistencyTests
{
    private const string Meta3 =
        "{\"documents\":{\"terms\":{\"requires_consent\":true,\"current_version\":3,\"required_version\":3},"
        + "\"dpa\":{\"requires_consent\":true,\"current_version\":1,\"required_version\":1},"
        + "\"privacy\":{\"requires_consent\":true,\"current_version\":1,\"required_version\":1}}}";

    /// <summary>Resolves what a sign-up form shows, records exactly that, and asks the gate.</summary>
    private static async Task<ConsentStatus> SignUpThenGateAsync(string docs, string? meta)
    {
        var options = new P4BackofficeProductOptions
        {
            Modules = { Docs = docs, Consents = "On" },
            BaseUrl = OutboxTestHost.BaseUrl,
            SecretKey = OutboxTestHost.SecretKey,
            Environment = "test",
            DisabledReason = BackofficeDisabledReason.None,
        };
        using var host = new OutboxTestHost(null, services =>
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options)));
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (meta != null)
        {
            await BackofficeStateStore.SetAsync(db, BackofficeStateKeys.DocsMeta, meta, DateTimeOffset.UtcNow);
        }

        var requirements = ConsentRequirements.From(options.Consents);
        var shown = await new ConsentDocumentResolver(
            db, new Mock<ILegalDocumentSource>().Object, scope.ServiceProvider, NullLogger<ConsentDocumentResolver>.Instance)
            .ResolveAsync(requirements, "en");

        var userId = Guid.NewGuid();
        foreach (var doc in shown)
        {
            db.ConsentRecords.Add(new ConsentRecord
            {
                UserId = userId, SubjectRef = "dotnet:user:" + userId, Document = doc.Document, Purpose = "terms",
                Version = doc.Version, Action = doc.Action, OccurredAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();

        var provider = new ConsentStatusProvider(
            db, new ConsentStatusCache(new MemoryCache(new MemoryCacheOptions())), Microsoft.Extensions.Options.Options.Create(options));
        return await provider.GetAsync(userId);
    }

    [Fact]
    public async Task DocsRolledBackToOff_WithStaleMetaV3_NoReconsentLoop() =>
        Assert.True((await SignUpThenGateAsync("Off", Meta3)).IsCurrent);

    [Fact]
    public async Task DocsOn_MetaRequiringV3_StillDemandsIt()
    {
        // With Docs On and nothing newer to show, the form refuses (503) rather than record v1;
        // a user whose record is v1 is outstanding for v3.
        await Assert.ThrowsAsync<ConsentVersionUnknownException>(() => SignUpThenGateAsync("On", Meta3));

        var status = ConsentStatusEvaluator.Evaluate(
            ConsentRequirements.From(new P4BackofficeProductOptions().Consents),
            DocumentsMeta.Parse(Meta3),
            [new LatestConsent("terms", "granted", 1, DateTimeOffset.UtcNow)]);
        Assert.Contains(status.Outstanding, o => o.Document == "terms" && o.RequiredVersion == 3);
    }

    [Fact]
    public async Task OnPremDocsOff_NoMeta_SignUpThenGate_IsCurrent() =>
        Assert.True((await SignUpThenGateAsync("Off", null)).IsCurrent);
}
