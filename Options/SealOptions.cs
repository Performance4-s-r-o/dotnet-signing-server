namespace DotNetSigningServer.Options;

public class SealOptions
{
    public bool Enabled { get; set; } = false;
    public string? PfxPath { get; set; }
    public string? PfxBase64 { get; set; }
    public string? PfxPassword { get; set; }
    public string Reason { get; set; } = "Corporate electronic seal";

    /// <summary>
    /// Written into the signature dictionary of every sealed document, so it must
    /// not default to a brand. An operator who configures their own certificate
    /// and overlooks this would otherwise stamp someone else's name into files
    /// that are already signed — and therefore no longer fixable.
    /// </summary>
    public string Location { get; set; } = "";
    public bool Visible { get; set; } = false;
}
