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
    }

    [Fact]
    public void Values_Parse()
    {
        Assert.Equal(20, EnvValues.Int(Config(" 20 "), "X"));
    }

    [Fact]
    public void Garbage_FailsLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => EnvValues.Int(Config("fifteen"), "X"));
    }

    [Theory]
    [InlineData("{{project.DOTNET_X}}", true)]
    [InlineData("{{environment.V0_X}}", true)]
    [InlineData(" {{ project.X }} ", true)]
    [InlineData("x{{project.Y}}", false)]
    [InlineData("{{}}", false)]
    [InlineData("01:00:00", false)]
    [InlineData(null, false)]
    public void Placeholder_Detection(string? raw, bool expected) =>
        Assert.Equal(expected, EnvValues.IsUnresolvedPlaceholder(raw));

    [Fact]
    public void Scrub_SelectsOnlyPlaceholderValues()
    {
        // Pure over a dictionary: the process environment is shared by parallel tests.
        var names = EnvValues.UnresolvedPlaceholderNames(new System.Collections.Hashtable
        {
            ["B_GIVE_UP"] = "{{project.DOTNET_P4_BACKOFFICE_OUTBOX_GIVE_UP_AFTER}}",
            ["A_CORS"] = "{{environment.DOTNET_CORS_ALLOWED_ORIGINS}}",
            ["REAL"] = "01:00:00",
            ["EMBEDDED"] = "x{{project.Y}}",
            ["EMPTY"] = "",
        });
        Assert.Equal(new[] { "A_CORS", "B_GIVE_UP" }, names);
    }
}
