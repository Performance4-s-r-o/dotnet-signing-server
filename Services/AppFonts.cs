using iText.IO.Font;
using iText.Kernel.Font;

namespace DotNetSigningServer.Services
{
    // Loads Liberation TTF fonts (metric-compatible replacements for the
    // PDF Standard 14 Helvetica/Times/Courier families) with IDENTITY_H
    // encoding so Czech diacritics ("ů", "ě", "š", "č", "ř", "ž", …) and
    // other Unicode glyphs render. Standard Type1 PDF fonts only ship
    // WinAnsi glyphs and silently drop anything else.
    //
    // Liberation fonts are bundled as Content files under Fonts/ and copied
    // next to the binary at build time.
    public enum AppFontFamily { Sans, Serif, Mono }

    public static class AppFonts
    {
        private static readonly string FontsDir = Path.Combine(AppContext.BaseDirectory, "Fonts");

        public static PdfFont Load(AppFontFamily family, bool bold = false, bool italic = false)
        {
            // Liberation Italic TTFs aren't always shipped — fall back to the
            // upright variant so missing italic doesn't break document
            // generation. The `Load(family, bold)` overload still works.
            string Filename(bool i) => (family, bold, i) switch
            {
                (AppFontFamily.Serif, false, true) => "LiberationSerif-Italic.ttf",
                (AppFontFamily.Serif, true, true) => "LiberationSerif-BoldItalic.ttf",
                (AppFontFamily.Serif, false, false) => "LiberationSerif-Regular.ttf",
                (AppFontFamily.Serif, true, false) => "LiberationSerif-Bold.ttf",
                (AppFontFamily.Mono, false, true) => "LiberationMono-Italic.ttf",
                (AppFontFamily.Mono, true, true) => "LiberationMono-BoldItalic.ttf",
                (AppFontFamily.Mono, false, false) => "LiberationMono-Regular.ttf",
                (AppFontFamily.Mono, true, false) => "LiberationMono-Bold.ttf",
                (_, false, true) => "LiberationSans-Italic.ttf",
                (_, true, true) => "LiberationSans-BoldItalic.ttf",
                (_, false, false) => "LiberationSans-Regular.ttf",
                (_, true, false) => "LiberationSans-Bold.ttf",
            };

            var preferred = Path.Combine(FontsDir, Filename(italic));
            var fallback = Path.Combine(FontsDir, Filename(false));
            var path = italic && !File.Exists(preferred) ? fallback : preferred;
            return PdfFontFactory.CreateFont(path, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
        }

        public static AppFontFamily FamilyFromLegacyName(string? name) => name switch
        {
            "Times" or "TimesRoman" or "TIMES_ROMAN" or "TIMES_BOLD" => AppFontFamily.Serif,
            "Courier" or "COURIER" or "COURIER_BOLD" => AppFontFamily.Mono,
            _ => AppFontFamily.Sans,
        };
    }
}
