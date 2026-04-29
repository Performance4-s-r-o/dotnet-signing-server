using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace DotNetSigningServer.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PdfFieldType
{
    [JsonStringEnumMemberName("text")]
    Text,
    [JsonStringEnumMemberName("image")]
    Image,
    [JsonStringEnumMemberName("barcode")]
    Barcode,
    [JsonStringEnumMemberName("signature")]
    Signature,
    [JsonStringEnumMemberName("table")]
    Table
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PdfFontName
{
    [JsonStringEnumMemberName("Helvetica")]
    Helvetica,
    [JsonStringEnumMemberName("Times-Roman")]
    TimesRoman,
    [JsonStringEnumMemberName("Courier")]
    Courier,
    [JsonStringEnumMemberName("Symbol")]
    Symbol,
    [JsonStringEnumMemberName("ZapfDingbats")]
    ZapfDingbats
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PdfFontWeight
{
    [JsonStringEnumMemberName("normal")]
    Normal,
    [JsonStringEnumMemberName("tiny")]
    Tiny,
    [JsonStringEnumMemberName("bold")]
    Bold
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PdfHorizontalAlign
{
    [JsonStringEnumMemberName("left")]
    Left,
    [JsonStringEnumMemberName("center")]
    Center,
    [JsonStringEnumMemberName("right")]
    Right
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PdfVerticalAlign
{
    [JsonStringEnumMemberName("center")]
    Center,
    [JsonStringEnumMemberName("top")]
    Top,
    [JsonStringEnumMemberName("bottom")]
    Bottom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PdfBorderStyle
{
    [JsonStringEnumMemberName("none")]
    None,
    [JsonStringEnumMemberName("dashed")]
    Dashed,
    [JsonStringEnumMemberName("filled")]
    Filled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PdfBarcodeFormat
{
    [JsonStringEnumMemberName("code128")]
    Code128,
    [JsonStringEnumMemberName("qr")]
    Qr,
    [JsonStringEnumMemberName("qrcode")]
    QrCode,
    [JsonStringEnumMemberName("qr-code")]
    QrCodeHyphen,
    [JsonStringEnumMemberName("datamatrix")]
    DataMatrix,
    [JsonStringEnumMemberName("data-matrix")]
    DataMatrixHyphen,
    [JsonStringEnumMemberName("dm")]
    Dm,
    [JsonStringEnumMemberName("pdf417")]
    Pdf417,
    [JsonStringEnumMemberName("ean13")]
    Ean13,
    [JsonStringEnumMemberName("ean-13")]
    Ean13Hyphen,
    [JsonStringEnumMemberName("ean8")]
    Ean8,
    [JsonStringEnumMemberName("ean-8")]
    Ean8Hyphen,
    [JsonStringEnumMemberName("upc")]
    Upc,
    [JsonStringEnumMemberName("upca")]
    UpcA,
    [JsonStringEnumMemberName("upc-a")]
    UpcAHyphen,
    [JsonStringEnumMemberName("code39")]
    Code39,
    [JsonStringEnumMemberName("code-39")]
    Code39Hyphen,
    [JsonStringEnumMemberName("itf")]
    Itf,
    [JsonStringEnumMemberName("interleaved2of5")]
    Interleaved2Of5,
    [JsonStringEnumMemberName("i2of5")]
    I2Of5
}

public class PdfFieldDefinition
{
    [Required]
    public string FieldName { get; set; } = string.Empty;
    [Required]
    public SignRect Rect { get; set; } = new();
    public int Page { get; set; } = 1;
    public float FontSize { get; set; } = 12;
    public PdfFontName? FontName { get; set; } = PdfFontName.Helvetica;
    public PdfFontWeight? FontWeight { get; set; } = PdfFontWeight.Normal;
    public PdfFieldType Type { get; set; } = PdfFieldType.Text;
    public PdfHorizontalAlign? HorizontalAlign { get; set; } = PdfHorizontalAlign.Left;
    public PdfVerticalAlign? VerticalAlign { get; set; } = PdfVerticalAlign.Center;
    public PdfBarcodeFormat? BarcodeFormat { get; set; } = PdfBarcodeFormat.Code128; // Used when Type == barcode
    public int? Columns { get; set; } // table
    public List<TableColumnDefinition>? TableColumns { get; set; } // table
    // Counter-clockwise rotation in degrees, applied around the rect's center.
    // 0 = no rotation; common values are 90/180/270.
    public float Rotation { get; set; } = 0;
    // Text decoration (text fields)
    public bool Italic { get; set; } = false;
    public bool Underline { get; set; } = false;
    /// <summary>Hex color string (#RRGGBB). Empty/null ⇒ default black.</summary>
    public string? TextColor { get; set; }
    /// <summary>Hex color string (#RRGGBB). Empty/null ⇒ transparent.</summary>
    public string? BackgroundColor { get; set; }
    /// <summary>Inner padding in PT applied uniformly to all four sides.</summary>
    public float Padding { get; set; } = 0;
    /// <summary>When true, paragraphs wrap and `LineHeight` controls leading.</summary>
    public bool Multiline { get; set; } = false;
    /// <summary>Leading (PT) used when Multiline is true.</summary>
    public float? LineHeight { get; set; }
    /// <summary>Border style for text fields. "none" / "solid" / "dashed" / "dotted".</summary>
    public string? BorderStyle { get; set; }
    public float BorderWidth { get; set; } = 0;
    /// <summary>Hex color string (#RRGGBB). Empty/null ⇒ default black.</summary>
    public string? BorderColor { get; set; }
}

public class TableColumnDefinition
{
    public string Name { get; set; } = string.Empty;
    public float? WidthPercent { get; set; }
    public float FontSize { get; set; } = 12;
    public PdfFontWeight? FontWeight { get; set; } = PdfFontWeight.Normal;
    public PdfBorderStyle? BorderStyle { get; set; } = PdfBorderStyle.None;
    public PdfHorizontalAlign? HorizontalAlign { get; set; } = PdfHorizontalAlign.Left;
    public PdfVerticalAlign? VerticalAlign { get; set; } = PdfVerticalAlign.Center;
}

public class PdfFieldValue
{
    [Required]
    public string FieldName { get; set; } = string.Empty;
    public string? Value { get; set; } = string.Empty;
    // For table fields: array of row arrays, each entry is a cell value.
    public List<List<string>>? TableValue { get; set; }
}

public class FillDataSet
{
    public List<PdfFieldValue> Data { get; set; } = new();
}

public class FillPdfInput
{
    public Guid? TemplateId { get; set; }
    public string PdfContent { get; set; } = string.Empty; // Base64
    public List<PdfFieldDefinition> Fields { get; set; } = new();
    public List<FillDataSet> Data { get; set; } = new();
}

public class FillPdfResponse
{
    public List<string> Files { get; set; } = new(); // Base64 PDFs
    public Guid? TemplateId { get; set; }
}

public class CreateTemplateInput
{
    public string PdfContent { get; set; } = string.Empty; // Base64
    public List<PdfFieldDefinition> Fields { get; set; } = new();
    public string? TemplateName { get; set; }
}

public class CreateTemplateResponse
{
    public Guid TemplateId { get; set; }
}

public class TemplateSummary
{
    public Guid TemplateId { get; set; }
    public string? Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int FieldCount { get; set; }
}

public class UpdateTemplateInput
{
    public string PdfContent { get; set; } = string.Empty; // Base64, optional
    public List<PdfFieldDefinition>? Fields { get; set; }
    public string? TemplateName { get; set; }
}

public class TemplateDetail
{
    public Guid TemplateId { get; set; }
    public string? Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string PdfContent { get; set; } = string.Empty;
    public List<PdfFieldDefinition> Fields { get; set; } = new();
}

public class StoredPdfTemplate
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    [Required]
    public string Base64Content { get; set; } = string.Empty;
    [Required]
    public string FieldsJson { get; set; } = string.Empty;
    public string? Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
