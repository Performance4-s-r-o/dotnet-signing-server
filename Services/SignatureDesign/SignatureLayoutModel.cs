using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DotNetSigningServer.Services.SignatureDesign
{
    /// <summary>
    /// Signature appearance model (design K5Dd): side zones, text elements, section order, direction,
    /// zone share, background and corner radius. A layout recipe only — the frame comes from whoever
    /// places the signature. Same JSON as the portal's <c>lib/signature-layout/model.ts</c>.
    /// </summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed class SignatureLayoutModel
    {
        public static readonly string[] SideKinds = { "sig", "logo", "image", "none" };
        public static readonly string[] SectionKeys = { "left", "text", "right" };
        public static readonly string[] TextElementKeys = { "name", "company", "reason", "location", "date", "text" };
        public static readonly string[] FontFamilies = { "Helvetica", "Times", "Courier" };
        public static readonly float[] FixedFontSizes = { 8, 10, 12 };
        public static readonly string[] BackgroundKinds = { "none", "color", "pattern", "image", "watermark" };
        public static readonly string[] PatternKinds = { "diag", "dots", "grid", "guilloche" };
        public static readonly string[] ImageFits = { "cover", "repeat" };
        public static readonly string[] Radii = { "none", "soft", "round" };
        public const int SplitMin = 10;
        public const int SplitMax = 60;
        public const int CustomTextMax = 120;
        public const int PrefixMax = 40;

        /// <summary>
        /// Strict JSON for the model (and the layout input): camelCase only, no numbers as strings. With the
        /// required members and unknown members refused it accepts what the portal's zod schema accepts.
        /// </summary>
        public static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
        };

        [JsonRequired]
        public int Version { get; set; } = 1;
        [JsonRequired]
        public string Left { get; set; } = "sig";
        [JsonRequired]
        public string Right { get; set; } = "none";
        [JsonRequired]
        public List<string> Order { get; set; } = new() { "left", "text", "right" };
        [JsonRequired]
        public string Direction { get; set; } = "row";
        [JsonRequired]
        public string TextDirection { get; set; } = "column";
        [JsonRequired]
        public int SplitLeft { get; set; } = 32;
        [JsonRequired]
        public int SplitRight { get; set; } = 26;
        [JsonRequired]
        public SignatureTextElements Elements { get; set; } = new();
        [JsonRequired]
        public string? CustomText { get; set; }
        [JsonRequired]
        public string FontFamily { get; set; } = "Helvetica";
        [JsonRequired]
        public float? FontSize { get; set; }
        [JsonRequired]
        public string TextColor { get; set; } = "#1f1f1f";
        [JsonRequired]
        public SignatureLayoutBackground Background { get; set; } = new();
        [JsonRequired]
        public string Radius { get; set; } = "none";

        private static readonly Regex Hex = new("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant);

        /// <summary>A seal stands for no person: no handwritten signature, no signer name. Reason or <c>null</c>.</summary>
        public string? SealViolation()
        {
            if (Left == "sig" || Right == "sig") return "A seal cannot show a handwritten signature.";
            if (Elements?.Name?.Enabled == true) return "A seal cannot show the signer's name.";
            return null;
        }

        /// <summary>The first problem with the model, or <c>null</c> when it is valid.</summary>
        public string? Validate()
        {
            if (Version != 1) return "version must be 1";
            if (!SideKinds.Contains(Left) || !SideKinds.Contains(Right)) return "left/right must be sig, logo, image or none";
            if (Left != "none" && Left == Right) return "the same side kind cannot fill both zones";
            if (Order is not { Count: 3 } || Order.Distinct().Count() != 3 || Order.Any(o => !SectionKeys.Contains(o)))
                return "order must be a permutation of left, text, right";
            if (Direction is not ("row" or "column")) return "direction must be row or column";
            if (TextDirection is not ("row" or "column")) return "textDirection must be row or column";
            if (SplitLeft is < SplitMin or > SplitMax || SplitRight is < SplitMin or > SplitMax)
                return $"splitLeft/splitRight must be {SplitMin}-{SplitMax}";
            if (Elements == null) return "elements are required";
            foreach (var key in TextElementKeys)
            {
                var el = Elements.Get(key);
                if (el == null) return $"elements.{key} is required";
                if (el.Prefix is { Length: > PrefixMax }) return $"elements.{key}.prefix is too long";
                if (el.Prefix != null && el.Prefix.Any(char.IsControl)) return $"elements.{key}.prefix must be one line";
            }
            if (CustomText is { Length: > CustomTextMax }) return "customText is too long";
            if (CustomText != null && CustomText.Any(char.IsControl)) return "customText must be one line";
            if (!FontFamilies.Contains(FontFamily)) return "fontFamily must be Helvetica, Times or Courier";
            if (FontSize.HasValue && !FixedFontSizes.Contains(FontSize.Value)) return "fontSize must be 8, 10, 12 or null";
            if (!Hex.IsMatch(TextColor ?? "")) return "textColor must be #rrggbb";
            var bg = Background;
            if (bg == null) return "background is required";
            if (!BackgroundKinds.Contains(bg.Kind)) return "background.kind is not supported";
            if (!Hex.IsMatch(bg.Color ?? "") || !Hex.IsMatch(bg.PatternColor ?? "")) return "background colours must be #rrggbb";
            if (!PatternKinds.Contains(bg.Pattern)) return "background.pattern is not supported";
            if (bg.Opacity is < 10 or > 100) return "background.opacity must be 10-100";
            if (!ImageFits.Contains(bg.ImageFit)) return "background.imageFit must be cover or repeat";
            if (!Radii.Contains(Radius)) return "radius must be none, soft or round";
            return null;
        }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed class SignatureTextElements
    {
        [JsonRequired]
        public SignatureTextElement Name { get; set; } = new() { Enabled = true, Bold = true };
        [JsonRequired]
        public SignatureTextElement Company { get; set; } = new() { Enabled = true };
        [JsonRequired]
        public SignatureTextElement Reason { get; set; } = new();
        [JsonRequired]
        public SignatureTextElement Location { get; set; } = new();
        [JsonRequired]
        public SignatureTextElement Date { get; set; } = new() { Enabled = true };
        [JsonRequired]
        public SignatureTextElement Text { get; set; } = new();

        public SignatureTextElement? Get(string key) => key switch
        {
            "name" => Name,
            "company" => Company,
            "reason" => Reason,
            "location" => Location,
            "date" => Date,
            "text" => Text,
            _ => null,
        };
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed class SignatureTextElement
    {
        [JsonRequired]
        public bool Enabled { get; set; }
        [JsonRequired]
        public bool Bold { get; set; }
        /// <summary><c>null</c> = the language's default label (reason and location only), <c>""</c> = none.</summary>
        [JsonRequired]
        public string? Prefix { get; set; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed class SignatureLayoutBackground
    {
        [JsonRequired]
        public string Kind { get; set; } = "none";
        [JsonRequired]
        public string Color { get; set; } = "#ffffff";
        [JsonRequired]
        public string Pattern { get; set; } = "diag";
        [JsonRequired]
        public string PatternColor { get; set; } = "#1b3a8c";
        [JsonRequired]
        public int Opacity { get; set; } = 60;
        [JsonRequired]
        public string ImageFit { get; set; } = "cover";
    }

    /// <summary>Values of the text elements; the custom text is part of the model.</summary>
    public sealed class SignatureLayoutValues
    {
        public string? Name { get; set; }
        public string? Company { get; set; }
        public string? Reason { get; set; }
        public string? Location { get; set; }
        public string? Date { get; set; }
    }

    /// <summary>Default labels in front of a value, e.g. "Reason:".</summary>
    public sealed class SignatureLayoutLabels
    {
        public string Reason { get; set; } = "Reason:";
        public string Location { get; set; } = "Location:";
    }

    /// <summary>Which images exist; a zone without its image is left out.</summary>
    public sealed class SignatureLayoutAssets
    {
        public bool Sig { get; set; }
        public bool Logo { get; set; }
        public bool Image { get; set; }
        public bool BackgroundImage { get; set; }
    }

    public sealed class SignatureLayoutInput
    {
        public SignatureLayoutModel Model { get; set; } = new();
        public double Width { get; set; }
        public double Height { get; set; }
        public SignatureLayoutValues Values { get; set; } = new();
        public SignatureLayoutLabels Labels { get; set; } = new();
        public SignatureLayoutAssets Assets { get; set; } = new();
    }
}
