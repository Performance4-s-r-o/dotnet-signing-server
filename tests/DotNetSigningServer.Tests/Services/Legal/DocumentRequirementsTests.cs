using DotNetSigningServer.Services.Backoffice.Documents;
using DotNetSigningServer.Services.Legal;

namespace DotNetSigningServer.Tests.Services.Legal;

public class DocumentRequirementsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static BackofficeDocumentVersion V(int version, string kind, int daysFromNow, string status = "effective") =>
        new(version, status, kind, Now.AddDays(daysFromNow), Now.AddDays(daysFromNow - 30), null);

    [Fact]
    public void RequiredVersion_IsTheNewestMaterialVersionInForce()
    {
        var versions = new[] { V(3, "material", -1, "effective"), V(2, "material", -100, "superseded"), V(1, "material", -300, "superseded") };

        Assert.Equal(3, DocumentRequirements.RequiredVersion(versions, Now));
    }

    [Theory]
    [InlineData("minor")]
    [InlineData("notice")]
    public void MinorAndNoticeVersions_DoNotRaiseTheRequiredVersion(string kind)
    {
        var versions = new[] { V(3, kind, -1), V(2, "material", -100, "superseded"), V(1, "material", -300, "superseded") };

        Assert.Equal(2, DocumentRequirements.RequiredVersion(versions, Now));
        Assert.Equal(3, DocumentRequirements.CurrentVersion(versions, Now));
    }

    [Fact]
    public void FutureMaterialVersion_IsNotRequiredYet()
    {
        var versions = new[] { V(3, "material", 14, "scheduled"), V(2, "material", -100) };

        Assert.Equal(2, DocumentRequirements.RequiredVersion(versions, Now));
        Assert.Equal(2, DocumentRequirements.CurrentVersion(versions, Now));
    }

    [Fact]
    public void FutureVersion_BecomesRequiredOnItsDate()
    {
        var versions = new[] { V(3, "material", 14, "scheduled"), V(2, "material", -100) };

        Assert.Equal(3, DocumentRequirements.RequiredVersion(versions, Now.AddDays(14)));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("pending_approval")]
    public void UnpublishedVersions_NeverCount(string status)
    {
        var versions = new[] { V(3, "material", -1, status), V(2, "material", -100) };

        Assert.Equal(2, DocumentRequirements.RequiredVersion(versions, Now));
    }

    [Fact]
    public void WithoutAMaterialVersion_TheFirstVersionInForceIsRequired()
    {
        var versions = new[] { V(2, "minor", -1), V(1, "notice", -100, "superseded") };

        Assert.Equal(1, DocumentRequirements.RequiredVersion(versions, Now));
    }

    [Fact]
    public void NothingInForce_RequiresNothing()
    {
        Assert.Null(DocumentRequirements.RequiredVersion(new[] { V(1, "material", 5, "scheduled") }, Now));
        Assert.Null(DocumentRequirements.RequiredVersion(Array.Empty<BackofficeDocumentVersion>(), Now));
    }

    [Fact]
    public void Meta_CarriesConsentFlagCurrentRequiredAndUpcoming()
    {
        var upcoming = V(4, "material", 20, "scheduled") with { Summary = "New liability cap" };
        var summary = new BackofficeDocumentSummary("terms", "Terms", RequiresConsent: true, Current: V(3, "minor", -1), Upcoming: upcoming);

        var meta = DocumentRequirements.Meta(summary, new[] { upcoming, V(3, "minor", -1), V(2, "material", -50, "superseded") }, Now);

        Assert.True(meta.RequiresConsent);
        Assert.Equal(3, meta.CurrentVersion);
        Assert.Equal(2, meta.RequiredVersion);
        Assert.NotNull(meta.Upcoming);
        Assert.Equal(4, meta.Upcoming!.Version);
        Assert.Equal("material", meta.Upcoming.ChangeKind);
        Assert.Equal(Now.AddDays(20), meta.Upcoming.EffectiveFrom);
    }

    [Fact]
    public void DocumentsMeta_RoundTripsAsSnakeCaseJson()
    {
        var meta = new DocumentsMeta(Now, new Dictionary<string, DocumentMeta>
        {
            ["terms"] = new(true, 3, 2, new DocumentUpcomingMeta(4, "material", Now.AddDays(20), null)),
        });

        var json = meta.ToJson();
        Assert.Contains("\"requires_consent\":true", json);
        Assert.Contains("\"required_version\":2", json);
        Assert.Contains("\"current_version\":3", json);

        var parsed = DocumentsMeta.Parse(json);
        Assert.NotNull(parsed);
        Assert.Equal(meta.Documents["terms"], parsed!.Documents["terms"]);
        Assert.Null(DocumentsMeta.Parse("not json"));
        Assert.Null(DocumentsMeta.Parse(null));
    }
}
