namespace DotNetSigningServer.Services.SignatureLayout
{
    // 1:1 port of v0-share-point-document-signing/lib/signature-layout/types.ts
    // Keep structurally identical — shared-spec refactors land here and on the TS side together.

    public enum ColumnKind { Left, Middle, Right }

    public enum RowKind { Description, Reason, Location, Date, Signer, Company }

    public class LayoutAppearance
    {
        public string FontFamily { get; set; } = "Helvetica";
        public float FontSize { get; set; } = 10f;
        public bool AutoFontSize { get; set; } = true;
        public bool ShowReason { get; set; } = true;
        public bool ShowLocation { get; set; } = true;
        public bool ShowDate { get; set; } = true;
        public bool ShowSignerName { get; set; } = true;
        public bool ShowCompanyName { get; set; } = true;
        public bool ShowSignature { get; set; } = true;
        public bool ShowCompanyLogo { get; set; } = true;
    }

    public class LayoutLabels
    {
        public string Reason { get; set; } = "Reason";
        public string Location { get; set; } = "Location";
        public string Date { get; set; } = "Date";
        public string Signer { get; set; } = "Signer";
        public string Company { get; set; } = "Company";
    }

    public class LayoutValues
    {
        public string? Description { get; set; }
        public string? Reason { get; set; }
        public string? Location { get; set; }
        public string? Date { get; set; }
        public string? Signer { get; set; }
        public string? Company { get; set; }
    }

    public class LayoutAssets
    {
        public bool HasSignature { get; set; }
        public bool HasCompanyLogo { get; set; }
        public bool HasStamp { get; set; }
    }

    public class LayoutInput
    {
        public float BoxWidthPt { get; set; }
        public float BoxHeightPt { get; set; }
        public bool AutoHeight { get; set; }
        public LayoutAppearance Appearance { get; set; } = new();
        public LayoutLabels Labels { get; set; } = new();
        public LayoutValues Values { get; set; } = new();
        public LayoutAssets Assets { get; set; } = new();
        public float PaddingPt { get; set; } = 4f;
        public float ColumnGapPt { get; set; } = 3f;
        public float LineHeightMul { get; set; } = 1.25f;
    }

    public class LayoutColumn
    {
        public ColumnKind Kind { get; set; }
        public float Weight { get; set; }
        public float WidthPt { get; set; }
        public float XPt { get; set; }
    }

    public class LayoutRow
    {
        public RowKind Kind { get; set; }
        public string Text { get; set; } = "";
    }

    public class LayoutResult
    {
        public float BoxWidthPt { get; set; }
        public float BoxHeightPt { get; set; }
        public List<LayoutColumn> Columns { get; set; } = new();
        public List<LayoutRow> Rows { get; set; } = new();
        public float FontSizePt { get; set; }
        public float LineHeightPt { get; set; }
        public float PaddingPt { get; set; }
        public float ColumnGapPt { get; set; }
    }

    public class ColumnPresence
    {
        public bool Left { get; set; }
        public bool Middle { get; set; }
        public bool Right { get; set; }
    }
}
