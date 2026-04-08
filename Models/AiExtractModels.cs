using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

public class AiExtractDataInput
{
    [Required]
    public string PdfContent { get; set; } = string.Empty; // Base64

    [Required]
    public List<AiExtractColumnDefinition> Columns { get; set; } = new();
}

public class AiExtractColumnDefinition
{
    [Required]
    public string Key { get; set; } = string.Empty;

    [Required]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }
}

public class AiExtractDataResponse
{
    public List<AiExtractedValue> Values { get; set; } = new();
}

public class AiExtractedValue
{
    public string Key { get; set; } = string.Empty;
    public string? Result { get; set; }
}
