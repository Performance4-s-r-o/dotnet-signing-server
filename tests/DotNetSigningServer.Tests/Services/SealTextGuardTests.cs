using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Services;

namespace DotNetSigningServer.Tests.Services;

public class SealTextGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Schvaluji dokument")]
    [InlineData("Souhlasím se zněním – č. 12/2026 (Praha, ČR)")]
    [InlineData("Approved by Åsa, 日本語")]
    public void Ordinary_text_is_allowed(string? value) => Assert.True(SealTextGuard.IsSafe(value, 256));

    [Fact]
    public void Astral_letters_and_emoji_without_joiners_are_allowed()
    {
        Assert.True(SealTextGuard.IsSafe("ok \U0001F600 \U00020000", 256));
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\tb")]
    [InlineData("a\0b")]
    [InlineData("a\u007Fb")]
    [InlineData("a‮b")]
    [InlineData("a​b")]
    [InlineData("a‍b")]
    [InlineData("a b")]
    [InlineData("ab")]
    [InlineData("a͏b")]
    [InlineData("aᅟb")]
    [InlineData("a឴b")]
    [InlineData("a᠎b")]
    [InlineData("a⠀b")]
    [InlineData("aㅤb")]
    [InlineData("a️b")]
    [InlineData("aﾠb")]
    [InlineData("a�b")]
    public void Control_invisible_and_malformed_characters_are_refused_by_the_guard(string value) =>
        Assert.False(SealTextGuard.IsSafe(value, 256));

    [Fact]
    public void Astral_invisible_characters_are_refused()
    {
        Assert.False(SealTextGuard.IsSafe("a" + char.ConvertFromUtf32(0xE0041) + "b", 256));   // tag character
        Assert.False(SealTextGuard.IsSafe("a" + char.ConvertFromUtf32(0xE0100) + "b", 256));   // variation selector
        Assert.False(SealTextGuard.IsSafe("a" + char.ConvertFromUtf32(0xF0000) + "b", 256));   // private use
        Assert.False(SealTextGuard.IsSafe("a" + char.ConvertFromUtf32(0x1D173) + "b", 256));   // musical format
        Assert.False(SealTextGuard.IsSafe("a" + char.ConvertFromUtf32(0x1FFFE) + "b", 256));   // unassigned
    }

    [Fact]
    public void Separators_and_lone_surrogates_are_refused()
    {
        Assert.False(SealTextGuard.IsSafe("a" + (char)0x2028 + "b", 256));
        Assert.False(SealTextGuard.IsSafe("a" + (char)0x2029 + "b", 256));
        Assert.False(SealTextGuard.IsSafe("a" + (char)0xD800 + "b", 256));
        Assert.False(SealTextGuard.IsSafe("a" + (char)0xDC00 + "b", 256));
    }

    [Fact]
    public void Tab_and_no_break_space_become_a_plain_space()
    {
        var (reason, location) = SealTextGuard.EnsureSafe("a\tb c", "Praha  1");
        Assert.Equal("a b c", reason);
        Assert.Equal("Praha  1", location);
    }

    [Fact]
    public void Newline_is_still_refused_after_normalising()
    {
        Assert.Equal(SealTextGuard.ReasonInvalid,
            Assert.Throws<ApiValidationException>(() => SealTextGuard.EnsureSafe("a\tb\nc", "")).Code);
    }

    [Fact]
    public void Length_limit_is_256_per_field()
    {
        var max = new string('a', 256);
        SealTextGuard.EnsureSafe(max, max);
        Assert.Equal(SealTextGuard.LocationInvalid,
            Assert.Throws<ApiValidationException>(() => SealTextGuard.EnsureSafe("ok", max + "a")).Code);
        Assert.Equal(SealTextGuard.ReasonInvalid,
            Assert.Throws<ApiValidationException>(() => SealTextGuard.EnsureSafe(max + "a", "ok")).Code);
    }
}
