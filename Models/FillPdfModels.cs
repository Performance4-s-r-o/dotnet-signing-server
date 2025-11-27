using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

public class PdfFieldDefinition
{
    [Required]
    public string FieldName { get; set; } = string.Empty;
    [Required]
    public SignRect Rect { get; set; } = new();
    public int Page { get; set; } = 1;
    public float FontSize { get; set; } = 12;
    public string FontName { get; set; } = "Helvetica";
    public string FontWeight { get; set; } = "normal";
    public string Type { get; set; } = "text"; // text, image, barcode, signature, table
    public string HorizontalAlign { get; set; } = "left"; // left, center, right
    public string VerticalAlign { get; set; } = "center"; // top, center, bottom
    public string? BarcodeFormat { get; set; } // Used when Type == barcode
    public int? Columns { get; set; } // table
    public List<TableColumnDefinition>? TableColumns { get; set; } // table
}

public class TableColumnDefinition
{
    public string Name { get; set; } = string.Empty;
    public float? WidthPercent { get; set; }
    public float FontSize { get; set; } = 12;
    public string FontWeight { get; set; } = "normal";
    public string BorderStyle { get; set; } = "none"; // none, dashed, filled
    public string HorizontalAlign { get; set; } = "left"; // left, center, right
    public string VerticalAlign { get; set; } = "center"; // top, center, bottom
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
