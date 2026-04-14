namespace DotNetSigningServer.Options;

public class ResendOptions
{
    public string? ApiKey { get; set; }
    public string From { get; set; } = "P4PDF <noreply@send.p4pdf.cz>";
    public string? ReplyTo { get; set; }
}
