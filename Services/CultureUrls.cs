using Microsoft.AspNetCore.Http;

namespace DotNetSigningServer.Services;

/// <summary>
/// Single source of truth for how a language shows up in a URL.
///
/// Every locale except English lives under its own path prefix (/cs/pricing,
/// /de/pricing …); English keeps the clean URL and is the x-default target.
/// A prefix — not a cookie or an Accept-Language header — decides which
/// language a page renders in, so every language version has one stable,
/// crawlable address and canonical/hreflang can never disagree with the body.
/// </summary>
public static class CultureUrls
{
    /// <summary>Locales the site is published in. First entry is the default.</summary>
    public static readonly string[] Supported = { "en", "cs", "de", "es" };

    public const string Default = "en";

    /// <summary>Path segment for a locale — empty for the default locale.</summary>
    public static string Prefix(string culture) =>
        IsDefault(culture) ? string.Empty : $"/{culture}";

    public static bool IsSupported(string? culture) =>
        !string.IsNullOrEmpty(culture) && Supported.Contains(culture);

    public static bool IsDefault(string? culture) =>
        string.IsNullOrEmpty(culture) || string.Equals(culture, Default, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits "/cs/pricing" into ("cs", "/pricing"). Returns false when the path
    /// carries no locale prefix, leaving the path untouched.
    /// </summary>
    public static bool TrySplit(PathString path, out string culture, out PathString rest)
    {
        foreach (var locale in Supported)
        {
            if (IsDefault(locale)) continue;
            if (path.StartsWithSegments($"/{locale}", StringComparison.OrdinalIgnoreCase, out var remaining))
            {
                culture = locale;
                rest = remaining.HasValue ? remaining : new PathString("/");
                return true;
            }
        }

        culture = Default;
        rest = path;
        return false;
    }

    /// <summary>Absolute URL of <paramref name="path"/> in one locale.</summary>
    public static string Absolute(string origin, string path, string culture)
    {
        var normalized = string.IsNullOrEmpty(path) ? "/" : path;
        var prefix = Prefix(culture);
        // "/cs" alone (not "/cs/") is the canonical home page of a locale.
        var tail = normalized == "/" && prefix.Length > 0 ? string.Empty : normalized;
        return $"{origin.TrimEnd('/')}{prefix}{tail}";
    }

    /// <summary>
    /// Best supported locale for an Accept-Language header, or null when the
    /// header is missing or names nothing we publish. Quality values are honoured
    /// so "de;q=0.9, cs;q=0.4" picks German.
    /// </summary>
    public static string? MatchAcceptLanguage(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;

        var best = (Culture: (string?)null, Quality: -1.0);
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var segments = part.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var tag = segments[0].Trim();
            if (tag.Length == 0 || tag == "*") continue;

            var quality = 1.0;
            foreach (var segment in segments.Skip(1))
            {
                var trimmed = segment.Trim();
                if (trimmed.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(trimmed[2..], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    quality = parsed;
                }
            }

            // "cs-CZ" and "cs" both mean Czech to us — we publish per language, not per region.
            var language = tag.Split('-')[0].ToLowerInvariant();
            if (!IsSupported(language)) continue;
            if (quality > best.Quality)
            {
                best = (language, quality);
            }
        }

        return best.Quality > 0 ? best.Culture : null;
    }
}
