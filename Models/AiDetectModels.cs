using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

public class AiDetectFieldsInput
{
    [Required]
    public string PdfContent { get; set; } = string.Empty; // Base64
    public string? Prompt { get; set; }
}

public class AiDetectFieldsResponse
{
    public List<PdfFieldDefinition> Fields { get; set; } = new();
}
