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
    public void Scrub_RemovesOnlyPlaceholderValues()
    {
        var placeholder = "P4_TEST_SCRUB_" + Guid.NewGuid().ToString("N");
        var real = "P4_TEST_KEEP_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(placeholder, "{{project.DOTNET_P4_BACKOFFICE_OUTBOX_GIVE_UP_AFTER}}");
        Environment.SetEnvironmentVariable(real, "01:00:00");
        try
        {
            var removed = EnvValues.ScrubUnresolvedPlaceholders();
            Assert.Contains(placeholder, removed);
            Assert.DoesNotContain(real, removed);
            Assert.Null(Environment.GetEnvironmentVariable(placeholder));
            Assert.Equal("01:00:00", Environment.GetEnvironmentVariable(real));
        }
        finally
        {
            Environment.SetEnvironmentVariable(placeholder, null);
            Environment.SetEnvironmentVariable(real, null);
        }
    }
}
