using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Consents;
using DotNetSigningServer.Services.Legal;

namespace DotNetSigningServer.Tests.Services.Consents;

public class ConsentStatusEvaluatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly ConsentRequirement[] Requirements =
    [
        new("terms", ConsentActions.Granted),
        new("dpa", ConsentActions.Granted),
        new("privacy", ConsentActions.Acknowledged),
    ];

    private static DocumentsMeta Meta(int termsRequired = 1, int dpaRequired = 1, int privacyRequired = 1) => new(T0, new()
    {
        ["terms"] = new DocumentMeta(true, termsRequired, termsRequired, null),
        ["dpa"] = new DocumentMeta(true, dpaRequired, dpaRequired, null),
        ["privacy"] = new DocumentMeta(true, privacyRequired, privacyRequired, null),
    });

    private static LatestConsent[] SignedUp(int version = 1) =>
    [
        new("terms", ConsentActions.Granted, version, T0),
        new("dpa", ConsentActions.Granted, version, T0),
        new("privacy", ConsentActions.Acknowledged, version, T0),
    ];

    [Fact]
    public void AllRecordedAtTheRequiredVersion_IsCurrent()
    {
        var status = ConsentStatusEvaluator.Evaluate(Requirements, Meta(), SignedUp());

        Assert.True(status.IsCurrent);
        Assert.True(status.HasAnyRecord);
    }

    [Fact]
    public void NewMaterialVersion_MakesTheDocumentOutstanding()
    {
        var status = ConsentStatusEvaluator.Evaluate(Requirements, Meta(termsRequired: 2), SignedUp());

        var outstanding = Assert.Single(status.Outstanding);
        Assert.Equal("terms", outstanding.Document);
        Assert.Equal(2, outstanding.RequiredVersion);
        Assert.Equal(1, outstanding.RecordedVersion);
    }

    [Fact]
    public void UserWithoutRecords_HasEverythingOutstanding()
    {
        var status = ConsentStatusEvaluator.Evaluate(Requirements, Meta(), []);

        Assert.Equal(3, status.Outstanding.Count);
        Assert.False(status.HasAnyRecord);
    }

    [Fact]
    public void WithoutDocsMeta_AnyRecordedVersionCounts()
    {
        Assert.True(ConsentStatusEvaluator.Evaluate(Requirements, null, SignedUp()).IsCurrent);
        Assert.Equal(3, ConsentStatusEvaluator.Evaluate(Requirements, null, []).Outstanding.Count);
    }

    [Fact]
    public void Revoked_IsOutstanding_AndTheNewestRecordWins()
    {
        var records = SignedUp().Append(new LatestConsent("dpa", ConsentActions.Revoked, 1, T0.AddDays(1))).ToList();
        Assert.Equal("dpa", Assert.Single(ConsentStatusEvaluator.Evaluate(Requirements, Meta(), records).Outstanding).Document);

        records.Add(new LatestConsent("dpa", ConsentActions.Granted, 1, T0.AddDays(2)));
        Assert.True(ConsentStatusEvaluator.Evaluate(Requirements, Meta(), records).IsCurrent);
    }

    [Fact]
    public void AcknowledgedDoesNotReplaceAGrant()
    {
        LatestConsent[] records =
        [
            new("terms", ConsentActions.Acknowledged, 1, T0),
            new("dpa", ConsentActions.Granted, 1, T0),
            new("privacy", ConsentActions.Granted, 1, T0),
        ];

        Assert.Equal("terms", Assert.Single(ConsentStatusEvaluator.Evaluate(Requirements, Meta(), records).Outstanding).Document);
    }

    [Fact]
    public void Upcoming_ListsOnlyFutureVersionsOfConsentDocuments()
    {
        var now = T0.AddDays(10);
        var meta = new DocumentsMeta(T0, new()
        {
            ["terms"] = new DocumentMeta(true, 1, 1, new DocumentUpcomingMeta(2, "material", now.AddDays(30), "New refund rules")),
            ["dpa"] = new DocumentMeta(true, 1, 1, new DocumentUpcomingMeta(2, "material", now.AddDays(-1), null)),
            ["sla"] = new DocumentMeta(false, 1, 1, new DocumentUpcomingMeta(2, "material", now.AddDays(5), null)),
        });

        var notice = Assert.Single(ConsentNoticeProvider.Upcoming(Requirements, meta, now));
        Assert.Equal("terms", notice.Document);
        Assert.Equal("terms-of-service", notice.Slug);
        Assert.Equal(2, notice.Version);
        Assert.Empty(ConsentNoticeProvider.Upcoming(Requirements, null, now));
    }
}
