namespace DotNetSigningServer.Models;

public class FindCodesInput
{
    public string PdfContent { get; set; } = string.Empty; // Base64
    public string CodeType { get; set; } = "any"; // qr, datamatrix, pdf417, aztec, any
}
