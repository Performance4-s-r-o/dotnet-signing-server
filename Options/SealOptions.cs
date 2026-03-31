namespace DotNetSigningServer.Options;

public class SealOptions
{
    public bool Enabled { get; set; } = false;
    public string? PfxPath { get; set; }
    public string? PfxBase64 { get; set; }
    public string? PfxPassword { get; set; }
    public string Reason { get; set; } = "Corporate electronic seal";
    public string Location { get; set; } = "P4PDF";
    public bool Visible { get; set; } = false;
}
