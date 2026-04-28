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

        public static PdfFont Load(AppFontFamily family, bool bold = false)
        {
            var fileName = (family, bold) switch
            {
                (AppFontFamily.Serif, false) => "LiberationSerif-Regular.ttf",
                (AppFontFamily.Serif, true) => "LiberationSerif-Bold.ttf",
                (AppFontFamily.Mono, false) => "LiberationMono-Regular.ttf",
                (AppFontFamily.Mono, true) => "LiberationMono-Bold.ttf",
                (_, false) => "LiberationSans-Regular.ttf",
                (_, true) => "LiberationSans-Bold.ttf",
            };
            var path = Path.Combine(FontsDir, fileName);
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
