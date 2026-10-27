using System.Globalization;
using System.Text;
using DotNetSigningServer.Exceptions;

namespace DotNetSigningServer.Services;

/// <summary>
/// <c>Reason</c> and <c>Location</c> of a seal go into the signature dictionary of a
/// document that cannot be corrected afterwards. Short text, no control or invisible
/// characters (line breaks, bidi overrides, zero-width, variation selectors, tag
/// characters), no lone surrogates. A tab and a no-break space are not refused but
/// turned into an ordinary space (<see cref="Normalize"/>). Empty is fine: the
/// configured default is used.
///
/// Known limit: emoji ZWJ sequences contain U+200D (a format character) and are refused.
/// </summary>
public static class SealTextGuard
{
    public const int MaxReasonLength = 256;
    public const int MaxLocationLength = 256;
    public const string ReasonInvalid = "SEAL_REASON_INVALID";
    public const string LocationInvalid = "SEAL_LOCATION_INVALID";

    /// <summary>Checks both fields and returns them with tab / no-break space replaced by a space.</summary>
    public static (string Reason, string Location) EnsureSafe(string? reason, string? location)
    {
        var r = Normalize(reason);
        var l = Normalize(location);
        if (!IsSafe(r, MaxReasonLength)) throw new ApiValidationException(ReasonInvalid);
        if (!IsSafe(l, MaxLocationLength)) throw new ApiValidationException(LocationInvalid);
        return (r, l);
    }

    public static string Normalize(string? value) =>
        string.IsNullOrEmpty(value) ? value ?? "" : value.Replace('\t', ' ').Replace(' ', ' ');

    public static bool IsSafe(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return true;
        if (value.Length > maxLength) return false;

        // A lone surrogate comes out of EnumerateRunes as U+FFFD, which is refused as well.
        foreach (var rune in value.EnumerateRunes())
        {
            if (IsRefused(rune)) return false;
        }
        return true;
    }

    private static bool IsRefused(Rune rune)
    {
        if (rune.Value == 0xFFFD) return true;
        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.Control:
            case UnicodeCategory.Format:
            case UnicodeCategory.LineSeparator:
            case UnicodeCategory.ParagraphSeparator:
            case UnicodeCategory.PrivateUse:
            case UnicodeCategory.OtherNotAssigned:
            case UnicodeCategory.Surrogate:
                return true;
            case UnicodeCategory.SpaceSeparator:
                if (rune.Value != ' ') return true;
                break;
        }
        return IsDefaultIgnorable(rune.Value);
    }

    /// <summary>Default_Ignorable_Code_Point characters that are not Cf: they render as nothing.</summary>
    private static bool IsDefaultIgnorable(int cp) =>
        cp == 0x034F || cp is >= 0x115F and <= 0x1160 || cp is >= 0x17B4 and <= 0x17B5
        || cp is >= 0x180B and <= 0x180F || cp == 0x2800 || cp == 0x3164
        || cp is >= 0xFE00 and <= 0xFE0F || cp == 0xFFA0
        || cp is >= 0xE0000 and <= 0xE0FFF || cp is >= 0x1BCA0 and <= 0x1BCA3;
}
