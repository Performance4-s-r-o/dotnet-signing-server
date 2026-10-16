using DotNetSigningServer.Options;
using Microsoft.Extensions.Configuration;

namespace DotNetSigningServer.Tests.Options;

/// <summary>#80: raw compose values — unset and unresolved Coolify placeholders fall back, garbage fails.</summary>
public class EnvValuesTests
{
    private static IConfiguration Config(string? value) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["X"] = value }).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("{{project.DOTNET_X}}")]
    public void Unset_GivesNull(string? raw)
    {
        Assert.Null(EnvValues.Int(Config(raw), "X"));
        Assert.Null(EnvValues.Instant(Config(raw), "X"));
    }

    [Fact]
    public void Values_Parse()
    {
        Assert.Equal(20, EnvValues.Int(Config(" 20 "), "X"));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero), EnvValues.Instant(Config("2026-10-20T12:00:00Z"), "X"));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero), EnvValues.Instant(Config("2026-10-20T12:00:00"), "X")); // no zone = UTC
    }

    [Fact]
    public void Garbage_FailsLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => EnvValues.Int(Config("fifteen"), "X"));
        Assert.Throws<InvalidOperationException>(() => EnvValues.Instant(Config("tomorrow"), "X"));
    }
}
