using System.Text.Json;

namespace DotNetSigningServer.Models
{
    public class FlowPipelineInput
    {
        public List<string> PdfContents { get; set; } = new(); // Base64 PDFs, optional if FillPdf is provided
        public FillPdfInput? FillPdf { get; set; }
        public List<FlowOperation> Flow { get; set; } = new();
    }

    public class FlowOperation
    {
        public string Action { get; set; } = string.Empty; // pdfa | attachment | presign | timestamp | sign-pfx
        public JsonElement Data { get; set; }
    }

    public class FlowRunResponse
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = "inprogress";
        public List<PendingSignature>? PendingSignatures { get; set; }
        public List<string>? Results { get; set; }
        public string? Error { get; set; }
    }

    public class PendingSignature
    {
        public string Id { get; set; } = string.Empty;
        public string HashToSign { get; set; } = string.Empty;
    }

    public class FlowSignRequest
    {
        public Guid FlowId { get; set; }
        public List<FlowSignedHash> Signatures { get; set; } = new();
    }

    public class FlowSignedHash
    {
        public string Id { get; set; } = string.Empty;
        public string SignedHash { get; set; } = string.Empty; // base64
    }
}
