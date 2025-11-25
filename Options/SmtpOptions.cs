namespace DotNetSigningServer.Options;

public class SmtpOptions
{
    public string Host { get; set; } = "smtp.example.com";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string FromAddress { get; set; } = "no-reply@example.com";
    public string FromName { get; set; } = "DotNet Signing Server";
    public string? Username { get; set; }
    public string? Password { get; set; }
}
