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
    public enum AppFontFamily {
        Sans,
        Serif,
        Mono,
        // Open-source extras bundled in Fonts/. All four variants
        // (Regular/Bold/Italic/BoldItalic) are shipped, so AppFonts.Load
        // can serve any combination without falling back.
        Inter,
        SourceSans3,
        Lora,
        Merriweather,
        JetBrainsMono,
    }

    public static class AppFonts
    {
        private static readonly string FontsDir = Path.Combine(AppContext.BaseDirectory, "Fonts");

        // Maps family enum to the file-name stem used in Fonts/. Suffix
        // -Regular / -Bold / -Italic / -BoldItalic is appended at lookup.
        private static string FamilyStem(AppFontFamily family) => family switch
        {
            AppFontFamily.Serif => "LiberationSerif",
            AppFontFamily.Mono => "LiberationMono",
            AppFontFamily.Inter => "Inter",
            AppFontFamily.SourceSans3 => "SourceSans3",
            AppFontFamily.Lora => "Lora",
            AppFontFamily.Merriweather => "Merriweather",
            AppFontFamily.JetBrainsMono => "JetBrainsMono",
            _ => "LiberationSans",
        };

        public static PdfFont Load(AppFontFamily family, bool bold = false, bool italic = false)
        {
            // Italic TTFs aren't guaranteed for every shipped family — fall
            // back to the upright variant so a missing italic file doesn't
            // break document generation. Bold is always shipped.
            string Filename(bool i)
            {
                var stem = FamilyStem(family);
                var variant = (bold, i) switch
                {
                    (true, true) => "BoldItalic",
                    (true, false) => "Bold",
                    (false, true) => "Italic",
                    _ => "Regular",
                };
                return $"{stem}-{variant}.ttf";
            }

            var preferred = Path.Combine(FontsDir, Filename(italic));
            var fallback = Path.Combine(FontsDir, Filename(false));
            var path = italic && !File.Exists(preferred) ? fallback : preferred;
            return PdfFontFactory.CreateFont(path, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
        }

        public static AppFontFamily FamilyFromLegacyName(string? name) => name switch
        {
            "Times" or "TimesRoman" or "TIMES_ROMAN" or "TIMES_BOLD" or "Times New Roman" => AppFontFamily.Serif,
            "Courier" or "COURIER" or "COURIER_BOLD" => AppFontFamily.Mono,
            "Inter" => AppFontFamily.Inter,
            "Source Sans 3" or "SourceSans3" or "Source Sans" => AppFontFamily.SourceSans3,
            "Lora" => AppFontFamily.Lora,
            "Merriweather" => AppFontFamily.Merriweather,
            "JetBrains Mono" or "JetBrainsMono" => AppFontFamily.JetBrainsMono,
            _ => AppFontFamily.Sans,
        };
    }
}
