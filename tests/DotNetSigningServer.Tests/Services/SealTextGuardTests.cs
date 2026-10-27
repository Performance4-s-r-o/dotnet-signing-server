using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Services;

namespace DotNetSigningServer.Tests.Services;

public class SealTextGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Schvaluji dokument")]
    [InlineData("Souhlas\u00EDm se zn\u011Bn\u00EDm \u2013 \u010D. 12/2026 (Praha, \u010CR)")]
    [InlineData("Approved by \u00C5sa, \u65E5\u672C\u8A9E, emoji \U0001F600")]
    public void Ordinary_text_is_allowed(string? value) => Assert.True(SealTextGuard.IsSafe(value, 200));

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\tb")]
    [InlineData("a\0b")]
    [InlineData("a\u007Fb")]
    [InlineData("a\u202Eb")]
    [InlineData("a\u200Bb")]
    [InlineData("a\u00A0b")]
    [InlineData("a\uE000b")]
    public void Control_invisible_and_malformed_characters_are_refused(string value) =>
        Assert.False(SealTextGuard.IsSafe(value, 200));

    [Fact]
    public void Line_and_paragraph_separators_are_refused()
    {
        Assert.False(SealTextGuard.IsSafe("a" + (char)0x2028 + "b", 200));
        Assert.False(SealTextGuard.IsSafe("a" + (char)0x2029 + "b", 200));
        // Lone surrogates: high without low, low without high.
        Assert.False(SealTextGuard.IsSafe("a" + (char)0xD800 + "b", 200));
        Assert.False(SealTextGuard.IsSafe("a" + (char)0xDC00 + "b", 200));
    }

    [Fact]
    public void Length_limit_is_per_field()
    {
        Assert.True(SealTextGuard.IsSafe(new string('a', SealTextGuard.MaxLocationLength), SealTextGuard.MaxLocationLength));
        Assert.False(SealTextGuard.IsSafe(new string('a', SealTextGuard.MaxLocationLength + 1), SealTextGuard.MaxLocationLength));
        Assert.Equal(SealTextGuard.LocationInvalid,
            Assert.Throws<ApiValidationException>(() =>
                SealTextGuard.EnsureSafe("ok", new string('a', SealTextGuard.MaxLocationLength + 1))).Code);
        Assert.Equal(SealTextGuard.ReasonInvalid,
            Assert.Throws<ApiValidationException>(() =>
                SealTextGuard.EnsureSafe(new string('a', SealTextGuard.MaxReasonLength + 1), "ok")).Code);
    }
}
