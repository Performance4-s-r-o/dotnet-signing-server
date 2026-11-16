namespace DotNetSigningServer.Services.SignatureDesign
{
    // Result of SignatureLayoutEngine: absolute rects in pt, origin top left. Same JSON as the
    // portal's SignatureLayoutResult (golden cases compare the two).

    public sealed class LayoutRect
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
    }

    public sealed class LayoutZone
    {
        public string Section { get; set; } = "left";
        /// <summary>sig, logo or image; the image fits the rect keeping its aspect ratio, centred.</summary>
        public string Kind { get; set; } = "sig";
        public LayoutRect Rect { get; set; } = new();
    }

    public sealed class LayoutTextRun
    {
        /// <summary><c>null</c> = separator between elements on one line.</summary>
        public string? Element { get; set; }
        public string Text { get; set; } = "";
        public double X { get; set; }
        public double Width { get; set; }
        public bool Bold { get; set; }
        /// <summary>primary or muted.</summary>
        public string Tone { get; set; } = "primary";
    }

    public sealed class LayoutTextLine
    {
        public double Baseline { get; set; }
        public List<LayoutTextRun> Runs { get; set; } = new();
    }

    public sealed class LayoutTextBox
    {
        public LayoutRect Rect { get; set; } = new();
        public double FontSize { get; set; }
        public double LineHeight { get; set; }
        public string Align { get; set; } = "left";
        public List<LayoutTextLine> Lines { get; set; } = new();
    }

    /// <summary>One shape for every background kind; fields that do not apply stay null.</summary>
    public sealed class LayoutBackground
    {
        public string Kind { get; set; } = "none";
        public string? Color { get; set; }
        public string? Pattern { get; set; }
        public string? PatternColor { get; set; }
        public double? Alpha { get; set; }
        public double? Spacing { get; set; }
        public double? LineWidth { get; set; }
        public double? DotRadius { get; set; }
        public string? Fit { get; set; }
        public LayoutRect? Rect { get; set; }
    }

    public sealed class SignatureLayoutResult
    {
        public int Version { get; set; } = 1;
        public double Width { get; set; }
        public double Height { get; set; }
        /// <summary>full, narrow or tiny.</summary>
        public string Mode { get; set; } = "full";
        public string Direction { get; set; } = "row";
        public double Padding { get; set; }
        public double Radius { get; set; }
        public string FontFamily { get; set; } = "Helvetica";
        public string TextColor { get; set; } = "#1f1f1f";
        public LayoutBackground Background { get; set; } = new();
        public List<LayoutZone> Zones { get; set; } = new();
        public LayoutTextBox? Text { get; set; }
    }
}
