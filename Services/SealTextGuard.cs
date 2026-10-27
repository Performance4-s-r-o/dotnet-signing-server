using System.Globalization;
using DotNetSigningServer.Exceptions;

namespace DotNetSigningServer.Services;

/// <summary>
/// <c>Reason</c> and <c>Location</c> of a seal go into the signature dictionary of a
/// document that cannot be corrected afterwards. Short text, no control or invisible
/// characters (line breaks, bidi overrides, zero-width), no lone surrogates.
/// Empty is fine: the configured default is used.
/// </summary>
public static class SealTextGuard
{
    public const int MaxReasonLength = 200;
    public const int MaxLocationLength = 100;
    public const string ReasonInvalid = "SEAL_REASON_INVALID";
    public const string LocationInvalid = "SEAL_LOCATION_INVALID";

    public static void EnsureSafe(string? reason, string? location)
    {
        if (!IsSafe(reason, MaxReasonLength)) throw new ApiValidationException(ReasonInvalid);
        if (!IsSafe(location, MaxLocationLength)) throw new ApiValidationException(LocationInvalid);
    }

    public static bool IsSafe(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return true;
        if (value.Length > maxLength) return false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return false;
                i++;
                continue;
            }
            if (char.IsLowSurrogate(c)) return false;

            switch (char.GetUnicodeCategory(c))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                    return false;
                case UnicodeCategory.SpaceSeparator when c != ' ':
                    return false;
            }
        }
        return true;
    }
}
