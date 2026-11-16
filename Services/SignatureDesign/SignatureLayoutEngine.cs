using System.Globalization;
using System.Text;

namespace DotNetSigningServer.Services.SignatureDesign
{
    /// <summary>
    /// Layout engine of the signature appearance, a 1:1 port of the portal's
    /// <c>lib/signature-layout/engine.ts</c>. Returns absolute rects in pt (origin top left); the portal and
    /// SPFx preview and this server's renderer only draw them. The golden cases (tests) hold both in step:
    /// change both together, keeping the order of operations (numbers are rounded on output only).
    /// </summary>
    /// <remarks>
    /// Responsive rules: the frame never grows; a side zone takes its share (10-60 %), both together at most
    /// 70 %; the font shrinks with the height (and the width when "auto"), never under 6 pt, and what does not
    /// fit ends with an ellipsis; narrower than 170 pt the first side zone goes above the text and the second
    /// hides; lower than 50 pt (or narrower than 120 pt) only the signature (or another image) and the first
    /// line stay; background and watermark fill the whole frame, the corner radius follows the height.
    /// </remarks>
    public static class SignatureLayoutEngine
    {
        public const double LineHeight = 1.2;
        public const double Baseline = 0.95;
        public const double AutoFontCap = 14;
        public const double MinFont = 6;
        public const double ReadableFont = 8;
        public const double NarrowWidth = 170;
        public const double TinyHeight = 50;
        public const double TinyWidth = 120;
        public const double MaxSidesShare = 0.7;
        public const double NarrowSideShare = 0.45;
        public const double TinySideShare = 0.6;
        public const string Ellipsis = "\u2026";
        public const string Separator = " \u00b7 ";
        public const double MinFrame = 20;
        public const double MaxFrame = 1000;
        public const int MaxValueChars = 200;

        private static readonly HashSet<string> Muted = new() { "reason", "location", "date" };

        private sealed record Item(string Key, string Text, bool Bold);

        /// <summary>Same as JavaScript <c>Math.round(n * 100) / 100</c>.</summary>
        internal static double R2(double n) => Math.Floor(n * 100 + 0.5) / 100;

        private static LayoutRect Rect(double x, double y, double w, double h) =>
            new() { X = R2(x), Y = R2(y), W = R2(w), H = R2(h) };

        private static int JsRound(double n) => (int)Math.Floor(n + 0.5);

        /// <summary>
        /// Same as the portal's <c>normalizeText</c>: a lone surrogate becomes U+FFFD, then NFC, control, format
        /// and separator characters (tab, new line, U+FEFF…) one space, a character outside the font table (CJK,
        /// emoji, RTL…) "?", only spaces are trimmed, at most <see cref="MaxValueChars"/> code points.
        /// </summary>
        public static string NormalizeText(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            // EnumerateRunes turns a lone surrogate into U+FFFD, so Normalize never sees invalid UTF-16.
            var valid = string.Concat(raw.EnumerateRunes().Select(r => r.ToString())).Normalize(NormalizationForm.FormC);
            var sb = new StringBuilder(valid.Length);
            bool space = false;
            foreach (var rune in valid.EnumerateRunes())
            {
                var cat = Rune.GetUnicodeCategory(rune);
                if (cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.SpaceSeparator
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                {
                    if (!space) sb.Append(' ');
                    space = true;
                    continue;
                }
                space = false;
                sb.Append(FontMetrics.HasWidth(rune.Value) ? rune.ToString() : "?");
            }
            var text = sb.ToString().Trim(' ');
            var runes = text.EnumerateRunes().ToList();
            return runes.Count > MaxValueChars
                ? string.Concat(runes.Take(MaxValueChars).Select(r => r.ToString())).TrimEnd(' ')
                : text;
        }

        private static List<Item> BuildItems(SignatureLayoutModel model, SignatureLayoutValues values, SignatureLayoutLabels labels)
        {
            var items = new List<Item>();
            foreach (var key in SignatureLayoutModel.TextElementKeys)
            {
                var el = model.Elements.Get(key)!;
                if (!el.Enabled) continue;
                string? raw = key switch
                {
                    "text" => model.CustomText,
                    "name" => values.Name,
                    "company" => values.Company,
                    "reason" => values.Reason,
                    "location" => values.Location,
                    _ => values.Date,
                };
                var value = NormalizeText(raw);
                if (value.Length == 0) continue;
                string prefix = el.Prefix == null
                    ? key == "reason" ? labels.Reason : key == "location" ? labels.Location : ""
                    : NormalizeText(el.Prefix);
                items.Add(new Item(key, prefix.Length > 0 ? prefix + " " + value : value, el.Bold));
            }
            return items;
        }

        /// <summary>Text cut with an ellipsis to <paramref name="maxWidth"/>, per code point.</summary>
        public static string Ellipsize(string text, string family, bool bold, double size, double maxWidth)
        {
            if (FontMetrics.TextWidth(text, family, bold, size) <= maxWidth) return text;
            var chars = text.EnumerateRunes().Select(r => r.ToString()).ToList();
            while (chars.Count > 0)
            {
                chars.RemoveAt(chars.Count - 1);
                var candidate = string.Concat(chars).TrimEnd(' ') + Ellipsis;
                if (FontMetrics.TextWidth(candidate, family, bold, size) <= maxWidth) return candidate;
            }
            return "";
        }

        private static LayoutTextRun Run(Item item, string text, double x, double width) => new()
        {
            Element = item.Key,
            Text = text,
            X = R2(x),
            Width = R2(width),
            Bold = item.Bold,
            Tone = Muted.Contains(item.Key) ? "muted" : "primary",
        };

        private sealed record Box(double X, double Y, double W, double H);

        private static LayoutTextBox LayoutColumnText(List<Item> items, SignatureLayoutModel model, Box tr, string align)
        {
            var family = model.FontFamily;
            bool fixedSize = model.FontSize.HasValue;
            double cap = model.FontSize ?? AutoFontCap;
            double widest = 0;
            foreach (var it in items) widest = Math.Max(widest, FontMetrics.TextWidth(it.Text, family, it.Bold, 1));

            int n = items.Count;
            double size = MinFont;
            for (int q = JsRound(cap * 4); q >= MinFont * 4; q--)
            {
                double s = q / 4.0;
                if (n * s * LineHeight <= tr.H && (fixedSize || widest * s <= tr.W))
                {
                    size = s;
                    break;
                }
            }
            double lh = size * LineHeight;
            // A line that does not fit even at 6 pt is dropped, the first one too.
            int keep = Math.Min(n, (int)Math.Floor((tr.H + 1e-6) / lh));
            double top = tr.Y + (tr.H - keep * lh) / 2;

            var lines = new List<LayoutTextLine>();
            for (int i = 0; i < keep; i++)
            {
                var it = items[i];
                var text = Ellipsize(it.Text, family, it.Bold, size, tr.W);
                double w = FontMetrics.TextWidth(text, family, it.Bold, size);
                double x = align == "left" ? tr.X : tr.X + (tr.W - w) / 2;
                lines.Add(new LayoutTextLine { Baseline = R2(top + i * lh + size * Baseline), Runs = { Run(it, text, x, w) } });
            }
            return new LayoutTextBox { Rect = Rect(tr.X, tr.Y, tr.W, tr.H), FontSize = size, LineHeight = R2(lh), Align = align, Lines = lines };
        }

        private static List<List<int>> PackRows(List<Item> items, double[] widths, double sepWidth, double size, double maxWidth)
        {
            var rows = new List<List<int>>();
            var current = new List<int>();
            double used = 0;
            for (int i = 0; i < items.Count; i++)
            {
                double w = widths[i] * size;
                if (current.Count == 0)
                {
                    current.Add(i);
                    used = w;
                }
                else if (used + sepWidth * size + w <= maxWidth)
                {
                    current.Add(i);
                    used = used + sepWidth * size + w;
                }
                else
                {
                    rows.Add(current);
                    current = new List<int> { i };
                    used = w;
                }
            }
            if (current.Count > 0) rows.Add(current);
            return rows;
        }

        private static LayoutTextBox LayoutRowText(List<Item> items, SignatureLayoutModel model, Box tr, string align)
        {
            var family = model.FontFamily;
            double cap = model.FontSize ?? AutoFontCap;
            var widths = items.Select(it => FontMetrics.TextWidth(it.Text, family, it.Bold, 1)).ToArray();
            double sepWidth = FontMetrics.TextWidth(Separator, family, false, 1);

            // As few rows as possible while the font stays readable; otherwise the largest font whose rows fit.
            double size = MinFont;
            var rows = PackRows(items, widths, sepWidth, MinFont, tr.W);
            bool found = false;
            for (int allowed = 1; allowed <= items.Count && !found; allowed++)
            {
                for (int q = JsRound(cap * 4); q >= ReadableFont * 4; q--)
                {
                    double s = q / 4.0;
                    var candidate = PackRows(items, widths, sepWidth, s, tr.W);
                    if (candidate.Count <= allowed && candidate.Count * s * LineHeight <= tr.H)
                    {
                        size = s;
                        rows = candidate;
                        found = true;
                        break;
                    }
                }
            }
            for (int q = JsRound(cap * 4); q >= MinFont * 4 && !found; q--)
            {
                double s = q / 4.0;
                var candidate = PackRows(items, widths, sepWidth, s, tr.W);
                if (candidate.Count * s * LineHeight <= tr.H)
                {
                    size = s;
                    rows = candidate;
                    found = true;
                }
            }
            double lh = size * LineHeight;
            int keep = Math.Min(rows.Count, (int)Math.Floor((tr.H + 1e-6) / lh));
            double top = tr.Y + (tr.H - keep * lh) / 2;
            double sepPt = sepWidth * size;

            var lines = new List<LayoutTextLine>();
            for (int r = 0; r < keep; r++)
            {
                var row = rows[r];
                var parts = new List<(Item Item, string Text, double Width)>();
                foreach (var i in row)
                {
                    var it = items[i];
                    // Only an element alone on its row may be wider than the frame.
                    var text = row.Count == 1 ? Ellipsize(it.Text, family, it.Bold, size, tr.W) : it.Text;
                    parts.Add((it, text, FontMetrics.TextWidth(text, family, it.Bold, size)));
                }
                double rowWidth = 0;
                for (int p = 0; p < parts.Count; p++) rowWidth = rowWidth + (p > 0 ? sepPt : 0) + parts[p].Width;
                double x = align == "left" ? tr.X : tr.X + (tr.W - rowWidth) / 2;
                var runs = new List<LayoutTextRun>();
                for (int p = 0; p < parts.Count; p++)
                {
                    if (p > 0)
                    {
                        runs.Add(new LayoutTextRun { Element = null, Text = Separator, X = R2(x), Width = R2(sepPt), Bold = false, Tone = "muted" });
                        x = x + sepPt;
                    }
                    runs.Add(Run(parts[p].Item, parts[p].Text, x, parts[p].Width));
                    x = x + parts[p].Width;
                }
                lines.Add(new LayoutTextLine { Baseline = R2(top + r * lh + size * Baseline), Runs = runs });
            }
            return new LayoutTextBox { Rect = Rect(tr.X, tr.Y, tr.W, tr.H), FontSize = size, LineHeight = R2(lh), Align = align, Lines = lines };
        }

        private static LayoutBackground LayoutBackgroundOf(SignatureLayoutInput input)
        {
            var bg = input.Model.Background;
            double H = input.Height;
            double W = input.Width;
            double op = bg.Opacity / 100.0;
            double k = Math.Max(0.4, H / 200);
            switch (bg.Kind)
            {
                case "color":
                    return new LayoutBackground { Kind = "color", Color = bg.Color };
                case "pattern":
                    {
                        bool guilloche = bg.Pattern == "guilloche";
                        bool thin = guilloche || bg.Pattern == "grid";
                        return new LayoutBackground
                        {
                            Kind = "pattern",
                            Color = bg.Color,
                            Pattern = bg.Pattern,
                            PatternColor = bg.PatternColor,
                            Alpha = R2(0.35 * op),
                            Spacing = R2(guilloche ? Math.Max(3, 7 * k) : Math.Max(4, 10 * k)),
                            LineWidth = R2(thin ? 0.75 : Math.Max(1, 2 * k)),
                            DotRadius = R2(1.6 * k),
                        };
                    }
                case "image":
                    return input.Assets.BackgroundImage
                        ? new LayoutBackground { Kind = "image", Fit = bg.ImageFit, Alpha = R2(op) }
                        : new LayoutBackground { Kind = "none" };
                case "watermark":
                    {
                        if (!input.Assets.Logo) return new LayoutBackground { Kind = "none" };
                        double side = Math.Min(0.8 * H, 0.8 * W);
                        return new LayoutBackground { Kind = "watermark", Rect = Rect(Math.Max(0, W - W * 0.06 - side), (H - side) / 2, side, side), Alpha = R2(0.25 * op) };
                    }
                default:
                    return new LayoutBackground { Kind = "none" };
            }
        }

        public static SignatureLayoutResult Compute(SignatureLayoutInput input)
        {
            var model = input.Model;
            double W = input.Width;
            double H = input.Height;
            if (!(W >= MinFrame && W <= MaxFrame && H >= MinFrame && H <= MaxFrame))
                throw new ArgumentOutOfRangeException(nameof(input), $"Signature frame must be {MinFrame}-{MaxFrame} pt, got {W} x {H}.");
            double pad = Math.Max(3, Math.Min(W, H) * 0.06);
            double gap = pad;

            var items = BuildItems(model, input.Values, input.Labels);
            bool tiny = H < TinyHeight || W < TinyWidth;
            bool narrow = !tiny && W < NarrowWidth;

            string? SideKind(string pos)
            {
                if (pos == "text") return null;
                var kind = pos == "left" ? model.Left : model.Right;
                bool present = kind switch
                {
                    "sig" => input.Assets.Sig,
                    "logo" => input.Assets.Logo,
                    "image" => input.Assets.Image,
                    _ => false,
                };
                return present ? kind : null;
            }

            var sides = model.Order.Where(pos => SideKind(pos) != null).ToList();
            if (tiny)
            {
                items = items.Take(1).ToList();
                var sig = sides.FirstOrDefault(pos => SideKind(pos) == "sig");
                sides = sig != null ? new List<string> { sig } : sides.Take(1).ToList();
            }
            else if (narrow)
            {
                sides = sides.Take(1).ToList();
            }
            var inner = new Box(pad, pad, Math.Max(0, W - 2 * pad), Math.Max(0, H - 2 * pad));

            (string Direction, List<string> Sections, Dictionary<string, double> Sizes) Plan(bool hasText)
            {
                bool forcedColumn = (tiny || narrow) && W < NarrowWidth && sides.Count > 0 && hasText;
                string direction = model.Direction == "column" || forcedColumn ? "column" : "row";
                var sections = forcedColumn
                    ? new List<string> { sides[0], "text" }
                    : model.Order.Where(pos => pos == "text" ? hasText : sides.Contains(pos)).ToList();
                double mainTotal = Math.Max(0, (direction == "row" ? inner.W : inner.H) - gap * Math.Max(0, sections.Count - 1));

                var shares = new Dictionary<string, double>();
                double sideSum = 0;
                foreach (var pos in sections)
                {
                    if (pos == "text") continue;
                    double share = forcedColumn
                        ? tiny ? TinySideShare : NarrowSideShare
                        : (pos == "left" ? model.SplitLeft : model.SplitRight) / 100.0;
                    shares[pos] = share;
                    sideSum = sideSum + share;
                }
                var sizes = new Dictionary<string, double>();
                if (hasText)
                {
                    double scale = sideSum > MaxSidesShare ? MaxSidesShare / sideSum : 1;
                    double used = 0;
                    foreach (var pos in sections)
                    {
                        if (pos == "text") continue;
                        double size = mainTotal * shares[pos] * scale;
                        sizes[pos] = size;
                        used = used + size;
                    }
                    sizes["text"] = mainTotal - used;
                }
                else
                {
                    foreach (var pos in sections) sizes[pos] = sideSum > 0 ? (mainTotal * shares[pos]) / sideSum : 0;
                }
                return (direction, sections, sizes);
            }

            var planned = Plan(items.Count > 0);
            // A text band that cannot hold one line at 6 pt gets no room at all.
            if (items.Count > 0 && sides.Count > 0)
            {
                double band = planned.Direction == "column" ? planned.Sizes["text"] : inner.H;
                if (band < MinFont * LineHeight)
                {
                    items = new List<Item>();
                    planned = Plan(false);
                }
            }
            var (direction, sections, sizes) = planned;

            var zones = new List<LayoutZone>();
            LayoutTextBox? text = null;
            string align = direction == "column" ? "center" : "left";
            double cursor = direction == "row" ? inner.X : inner.Y;
            foreach (var pos in sections)
            {
                double size = sizes[pos];
                var r = direction == "row"
                    ? new Box(cursor, inner.Y, size, inner.H)
                    : new Box(inner.X, cursor, inner.W, size);
                if (pos == "text")
                {
                    text = model.TextDirection == "row"
                        ? LayoutRowText(items, model, r, align)
                        : LayoutColumnText(items, model, r, align);
                }
                else
                {
                    zones.Add(new LayoutZone { Section = pos, Kind = SideKind(pos)!, Rect = Rect(r.X, r.Y, r.W, r.H) });
                }
                cursor = cursor + size + gap;
            }

            double radius = Math.Min(Math.Min(model.Radius == "soft" ? H * 0.08 : model.Radius == "round" ? H * 0.2 : 0, W / 2), H / 2);
            return new SignatureLayoutResult
            {
                Version = 1,
                Width = R2(W),
                Height = R2(H),
                Mode = tiny ? "tiny" : narrow ? "narrow" : "full",
                Direction = direction,
                Padding = R2(pad),
                Radius = R2(radius),
                FontFamily = model.FontFamily,
                TextColor = model.TextColor,
                Background = LayoutBackgroundOf(input),
                Zones = zones,
                Text = text,
            };
        }
    }
}
