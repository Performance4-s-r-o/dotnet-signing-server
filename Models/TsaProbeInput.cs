namespace DotNetSigningServer.Models
{
    public class TsaProbeInput
    {
        public string TsaUrl { get; set; } = string.Empty;
        public string? TsaUsername { get; set; }
        public string? TsaPassword { get; set; }
    }
}
