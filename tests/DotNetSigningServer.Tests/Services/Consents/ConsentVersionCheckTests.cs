using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Consents;

namespace DotNetSigningServer.Tests.Services.Consents;

public class ConsentVersionCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameVersion_IsCurrent() =>
        Assert.Equal(ConsentVersionVerdict.Current, ConsentVersionCheck.Check(3, 3, Now.AddDays(-30), Now));

    [Fact]
    public void SameVersion_WithoutKnownDate_IsCurrent() =>
        Assert.Equal(ConsentVersionVerdict.Current, ConsentVersionCheck.Check(1, 1, null, Now));

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(10)]
    public void PreviousVersion_UpToTenMinutesAfterThePublication_IsAccepted(int minutes) =>
        Assert.Equal(ConsentVersionVerdict.PreviousWithinGrace,
            ConsentVersionCheck.Check(2, 3, Now.AddMinutes(-minutes), Now));

    [Theory]
    [InlineData(11)]
    [InlineData(60 * 24)]
    public void PreviousVersion_LaterThanTenMinutes_IsOutdated(int minutes) =>
        Assert.Equal(ConsentVersionVerdict.Outdated, ConsentVersionCheck.Check(2, 3, Now.AddMinutes(-minutes), Now));

    [Fact]
    public void PreviousVersion_WithoutKnownDate_IsOutdated() =>
        Assert.Equal(ConsentVersionVerdict.Outdated, ConsentVersionCheck.Check(2, 3, null, Now));

    [Fact]
    public void PreviousVersion_BeforeTheNewOneIsInForce_IsOutdated() =>
        // A clock skew must not open the window before the version is even in force.
        Assert.Equal(ConsentVersionVerdict.Outdated, ConsentVersionCheck.Check(2, 3, Now.AddMinutes(1), Now));

    [Theory]
    [InlineData(null)]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(-1)]
    public void MissingNewerOrInvalidVersion_IsOutdated(int? shown) =>
        Assert.Equal(ConsentVersionVerdict.Outdated, ConsentVersionCheck.Check(shown, 3, Now.AddMinutes(-1), Now));

    [Fact]
    public void Accept_ReturnsTheVersionToRecordPerDocument()
    {
        var documents = new[]
        {
            Document("terms", 3, Now.AddMinutes(-2)),
            Document("dpa", 1, Now.AddDays(-100)),
        };

        var accepted = ConsentVersionCheck.Accept(new Dictionary<string, int> { ["terms"] = 2, ["dpa"] = 1 }, documents, Now);

        Assert.NotNull(accepted);
        Assert.Equal(2, accepted!["terms"]);
        Assert.Equal(1, accepted["dpa"]);
    }

    [Fact]
    public void Accept_OneOutdatedDocument_RefusesTheForm()
    {
        var documents = new[]
        {
            Document("terms", 3, Now.AddMinutes(-20)),
            Document("dpa", 1, Now.AddDays(-100)),
        };

        Assert.Null(ConsentVersionCheck.Accept(new Dictionary<string, int> { ["terms"] = 2, ["dpa"] = 1 }, documents, Now));
        Assert.Null(ConsentVersionCheck.Accept(null, documents, Now));
    }

    internal static ConsentDocumentVersion Document(string type, int version, DateTimeOffset? since, string action = ConsentActions.Granted, string? hash = null) =>
        new(type, action, type, version, "en", hash, since, null, null);
}
