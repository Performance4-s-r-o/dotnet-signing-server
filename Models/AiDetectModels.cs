using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

public class AiDetectFieldsInput
{
    [Required]
    public string PdfContent { get; set; } = string.Empty; // Base64

    /// <summary>What to look for, in the user's words; passed to the model as data.</summary>
    [MaxLength(2000)]
    public string? Prompt { get; set; }

    /// <summary>First page to read (1-based); empty = 1.</summary>
    public int? PageFrom { get; set; }

    /// <summary>Last page to read; cut to the AI page limit.</summary>
    public int? PageTo { get; set; }
}

public class AiDetectFieldsResponse
{
    public List<PdfFieldDefinition> Fields { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    public int PagesProcessed { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;
}
