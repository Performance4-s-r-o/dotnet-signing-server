namespace DotNetSigningServer.Services.SignatureLayout
{
    /// <summary>
    /// 1:1 port of v0-share-point-document-signing/lib/signature-layout/.
    /// Produces the same LayoutResult as the TS module given identical input
    /// — that's how SPFx preview, v0 WYSIWYG, and this iText renderer stay in
    /// sync. Refactor in lockstep with the TS version.
    /// </summary>
    public static class SignatureLayoutCalculator
    {
        private const float AvgCharWidthRatio = 0.52f;
        private const float MinBoxHeightPt = 30f;
        private const float DefaultMinFontSize = 6f;
        private const float FontSizeResolution = 0.5f;

        // ----- columns --------------------------------------------------------

        public static ColumnPresence DetectColumnPresence(
            LayoutAssets assets,
            LayoutAppearance appearance,
            bool hasTextRows)
        {
            return new ColumnPresence
            {
                Left =
                    (appearance.ShowSignature && assets.HasSignature) ||
                    (appearance.ShowCompanyLogo && assets.HasCompanyLogo),
                Middle = hasTextRows,
                Right = assets.HasStamp,
            };
        }

        public static List<(ColumnKind Kind, float Weight)> ColumnWeights(ColumnPresence p)
        {
            if (p.Left && p.Middle && p.Right)
                return new() { (ColumnKind.Left, 1), (ColumnKind.Middle, 2), (ColumnKind.Right, 1) };
            if (p.Left && p.Middle)
                return new() { (ColumnKind.Left, 1), (ColumnKind.Middle, 3) };
            if (p.Middle && p.Right)
                return new() { (ColumnKind.Middle, 3), (ColumnKind.Right, 1) };
            if (p.Left && p.Right)
                return new() { (ColumnKind.Left, 1), (ColumnKind.Right, 1) };
            if (p.Middle) return new() { (ColumnKind.Middle, 1) };
            if (p.Left) return new() { (ColumnKind.Left, 1) };
            if (p.Right) return new() { (ColumnKind.Right, 1) };
            return new() { (ColumnKind.Middle, 1) };
        }

        public static List<LayoutColumn> ResolveColumns(
            List<(ColumnKind Kind, float Weight)> weights,
            float innerWidthPt,
            float columnGapPt)
        {
            float totalWeight = weights.Sum(w => w.Weight);
            float totalGap = columnGapPt * Math.Max(0, weights.Count - 1);
            float widthForCols = Math.Max(0, innerWidthPt - totalGap);

            float x = 0;
            var cols = new List<LayoutColumn>();
            foreach (var (kind, weight) in weights)
            {
                float widthPt = totalWeight > 0 ? (widthForCols * weight) / totalWeight : 0;
                cols.Add(new LayoutColumn { Kind = kind, Weight = weight, WidthPt = widthPt, XPt = x });
                x += widthPt + columnGapPt;
            }
            return cols;
        }

        // ----- rows -----------------------------------------------------------

        public static List<LayoutRow> BuildRows(
            LayoutAppearance appearance,
            LayoutLabels labels,
            LayoutValues values)
        {
            var specs = new (RowKind Kind, bool Show, string? Label, string? Value)[]
            {
                (RowKind.Description, true,                     null,            values.Description),
                (RowKind.Reason,      appearance.ShowReason,    labels.Reason,   values.Reason),
                (RowKind.Location,    appearance.ShowLocation,  labels.Location, values.Location),
                (RowKind.Date,        appearance.ShowDate,      labels.Date,     values.Date),
                (RowKind.Signer,      appearance.ShowSignerName,labels.Signer,   values.Signer),
                (RowKind.Company,     appearance.ShowCompanyName,labels.Company, values.Company),
            };

            var rows = new List<LayoutRow>();
            foreach (var spec in specs)
            {
                if (!spec.Show) continue;
                var value = (spec.Value ?? "").Trim();
                if (string.IsNullOrEmpty(value)) continue;
                rows.Add(new LayoutRow
                {
                    Kind = spec.Kind,
                    Text = spec.Label != null ? $"{spec.Label}: {value}" : value,
                });
            }
            return rows;
        }

        // ----- font fit -------------------------------------------------------

        public static float EstimateCharWidth(float fontSizePt) => fontSizePt * AvgCharWidthRatio;

        public static int WrapCount(string text, float widthPt, float fontSizePt)
        {
            if (string.IsNullOrEmpty(text)) return 1;
            float charWidth = EstimateCharWidth(fontSizePt);
            if (charWidth <= 0) return 1;
            int charsPerLine = Math.Max(1, (int)Math.Floor(widthPt / charWidth));
            return Math.Max(1, (int)Math.Ceiling((double)text.Length / charsPerLine));
        }

        private static int TotalLinesAt(List<LayoutRow> rows, float widthPt, float fontSizePt)
        {
            int total = 0;
            foreach (var row in rows) total += WrapCount(row.Text, widthPt, fontSizePt);
            return total;
        }

        public class FontFitResult
        {
            public float FontSizePt { get; set; }
            public float LineHeightPt { get; set; }
            public int TotalLines { get; set; }
            public float ContentHeightPt { get; set; }
        }

        public static FontFitResult FitFont(
            List<LayoutRow> rows,
            float columnWidthPt,
            float columnHeightPt,
            bool autoHeight,
            bool autoFontSize,
            float fallbackFontSize,
            float lineHeightMul,
            float? minFontSize = null,
            float? maxFontSize = null)
        {
            float min = minFontSize ?? DefaultMinFontSize;
            float max = maxFontSize ?? fallbackFontSize;

            if (!autoFontSize || rows.Count == 0 || autoHeight)
            {
                float size = fallbackFontSize;
                float lineHeight = size * lineHeightMul;
                int lines = TotalLinesAt(rows, columnWidthPt, size);
                return new FontFitResult
                {
                    FontSizePt = size,
                    LineHeightPt = lineHeight,
                    TotalLines = lines,
                    ContentHeightPt = lines * lineHeight,
                };
            }

            float lo = min, hi = max, best = min;
            while (hi - lo > FontSizeResolution)
            {
                float mid = (lo + hi) / 2f;
                int lines = TotalLinesAt(rows, columnWidthPt, mid);
                float contentHeight = lines * mid * lineHeightMul;
                if (contentHeight <= columnHeightPt) { best = mid; lo = mid; }
                else hi = mid;
            }

            float fontSize = (float)Math.Floor(best * 2) / 2f;
            float resolvedLineHeight = fontSize * lineHeightMul;
            int resolvedLines = TotalLinesAt(rows, columnWidthPt, fontSize);
            return new FontFitResult
            {
                FontSizePt = fontSize,
                LineHeightPt = resolvedLineHeight,
                TotalLines = resolvedLines,
                ContentHeightPt = resolvedLines * resolvedLineHeight,
            };
        }

        // ----- compose --------------------------------------------------------

        public static LayoutResult ComputeLayout(LayoutInput input)
        {
            var rows = BuildRows(input.Appearance, input.Labels, input.Values);
            var presence = DetectColumnPresence(input.Assets, input.Appearance, rows.Count > 0);
            var weights = ColumnWeights(presence);

            float innerWidth = Math.Max(0, input.BoxWidthPt - input.PaddingPt * 2);
            var columns = ResolveColumns(weights, innerWidth, input.ColumnGapPt);
            var middle = columns.FirstOrDefault(c => c.Kind == ColumnKind.Middle);

            float innerHeightFixed = Math.Max(0, input.BoxHeightPt - input.PaddingPt * 2);
            float textAreaWidth = middle?.WidthPt ?? innerWidth;

            var fit = FitFont(
                rows,
                textAreaWidth,
                innerHeightFixed,
                input.AutoHeight,
                input.Appearance.AutoFontSize,
                input.Appearance.FontSize,
                input.LineHeightMul);

            float finalHeight = input.BoxHeightPt;
            if (input.AutoHeight)
            {
                // AutoHeight = grow-only. The design's box height is the
                // starting point / minimum; we expand when text won't fit,
                // but never shrink below it (shrinking would violate the
                // chosen aspect ratio — e.g. a 300×100 design rendering as
                // 300×54 when only two rows are present).
                float needed = fit.ContentHeightPt + input.PaddingPt * 2;
                finalHeight = Math.Max(input.BoxHeightPt, Math.Max(MinBoxHeightPt, needed));
            }

            return new LayoutResult
            {
                BoxWidthPt = input.BoxWidthPt,
                BoxHeightPt = finalHeight,
                Columns = columns,
                Rows = rows,
                FontSizePt = fit.FontSizePt,
                LineHeightPt = fit.LineHeightPt,
                PaddingPt = input.PaddingPt,
                ColumnGapPt = input.ColumnGapPt,
            };
        }

        // ----- labels ---------------------------------------------------------

        public static LayoutLabels GetLabelsForLocale(string? locale)
        {
            var key = NormalizeLocale(locale);
            return key switch
            {
                "cs" => new LayoutLabels
                {
                    Reason = "Důvod",
                    Location = "Místo",
                    Date = "Datum",
                    Signer = "Podepsal",
                    Company = "Společnost",
                },
                "de" => new LayoutLabels
                {
                    Reason = "Grund",
                    Location = "Ort",
                    Date = "Datum",
                    Signer = "Unterzeichner",
                    Company = "Unternehmen",
                },
                "es" => new LayoutLabels
                {
                    Reason = "Motivo",
                    Location = "Lugar",
                    Date = "Fecha",
                    Signer = "Firmante",
                    Company = "Empresa",
                },
                _ => new LayoutLabels
                {
                    Reason = "Reason",
                    Location = "Location",
                    Date = "Date",
                    Signer = "Signer",
                    Company = "Company",
                },
            };
        }

        public static string NormalizeLocale(string? locale)
        {
            if (string.IsNullOrWhiteSpace(locale)) return "en";
            var baseTag = locale.Split('-')[0].ToLowerInvariant();
            return baseTag switch
            {
                "en" or "cs" or "de" or "es" => baseTag,
                _ => "en",
            };
        }
    }
}
