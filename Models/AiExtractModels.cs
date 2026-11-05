using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

/// <summary>Body of <c>POST /api/ai/extract-data</c> (contract v2).</summary>
public class AiExtractDataInput
{
    [Required]
    public string PdfContent { get; set; } = string.Empty; // Base64

    [Required]
    public List<AiExtractColumnDefinition> Columns { get; set; } = new();

    /// <summary>Earlier corrections, passed to the model as data (never as instructions).</summary>
    public List<AiExtractExample>? Examples { get; set; }

    /// <summary>First page to read (1-based); empty = 1.</summary>
    public int? PageFrom { get; set; }

    /// <summary>Last page to read; empty = the last page. The range is cut to the AI page limit.</summary>
    public int? PageTo { get; set; }
}

public class AiExtractColumnDefinition
{
    [Required]
    public string Key { get; set; } = string.Empty;

    [Required]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary><c>text</c> (default), <c>number</c>, <c>integer</c>, <c>date</c>, <c>boolean</c>, <c>choice</c>, <c>email</c>, <c>iban</c>.</summary>
    public string? Type { get; set; }

    /// <summary>Allowed values of a <c>choice</c> column.</summary>
    public List<string>? Choices { get; set; }

    /// <summary>Longest text value; empty = 1000.</summary>
    public int? MaxLength { get; set; }
}

public class AiExtractExample
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public class AiExtractDataResponse
{
    public List<AiExtractedValue> Values { get; set; } = new();

    /// <summary><c>hidden_text_detected</c>, <c>pages_truncated</c>.</summary>
    public List<string> Warnings { get; set; } = new();

    public int PagesProcessed { get; set; }

    /// <summary>Provider that answered (<c>anthropic</c>, <c>google</c>).</summary>
    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;
}

public class AiExtractedValue
{
    public string Key { get; set; } = string.Empty;

    /// <summary>The value as text; null when absent, uncertain or <see cref="Invalid"/>.</summary>
    public string? Result { get; set; }

    /// <summary><c>high</c> only when the evidence was found on the page's text layer and no hidden text was found.</summary>
    public string Confidence { get; set; } = "low";

    /// <summary>Page of the original document the value was found on.</summary>
    public int? Page { get; set; }

    /// <summary>Quoted snippet (≤ 200 characters).</summary>
    public string? Evidence { get; set; }

    /// <summary>The model's value did not pass the column's type or format; <see cref="Result"/> is then null.</summary>
    public bool Invalid { get; set; }
}
