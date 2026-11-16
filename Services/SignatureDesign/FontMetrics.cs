using System.Reflection;
using System.Text;
using System.Text.Json;

namespace DotNetSigningServer.Services.SignatureDesign
{
    /// <summary>
    /// Advance widths (1/1000 em) of the Liberation fonts the renderer draws with, the same table
    /// the portal measures with (tools/gen-font-metrics.py). A character outside the table is as wide as "n".
    /// </summary>
    public static class FontMetrics
    {
        private sealed record Table(int Fallback, int[] Widths);

        private static readonly Lazy<(Dictionary<int, int> Index, Dictionary<string, Table> Fonts)> Data = new(Load);

        private static (Dictionary<int, int>, Dictionary<string, Table>) Load()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SignatureDesign.font-metrics.json")
                ?? throw new InvalidOperationException("font-metrics.json is not embedded");
            using var doc = JsonDocument.Parse(stream);
            var index = new Dictionary<int, int>();
            int i = 0;
            foreach (var rune in doc.RootElement.GetProperty("codepoints").GetString()!.EnumerateRunes())
                index[rune.Value] = i++;
            var fonts = new Dictionary<string, Table>();
            foreach (var font in doc.RootElement.GetProperty("fonts").EnumerateObject())
            {
                var widths = font.Value.GetProperty("widths").EnumerateArray().Select(w => w.GetInt32()).ToArray();
                fonts[font.Name] = new Table(font.Value.GetProperty("fallback").GetInt32(), widths);
            }
            return (index, fonts);
        }

        /// <summary>Width of <paramref name="text"/> in pt at <paramref name="size"/>, per code point.</summary>
        public static double TextWidth(string text, string family, bool bold, double size)
        {
            var (index, fonts) = Data.Value;
            var table = fonts[$"{family}-{(bold ? "Bold" : "Regular")}"];
            double units = 0;
            foreach (var rune in text.EnumerateRunes())
                units += index.TryGetValue(rune.Value, out var idx) ? table.Widths[idx] : table.Fallback;
            return (units * size) / 1000;
        }
    }
}
