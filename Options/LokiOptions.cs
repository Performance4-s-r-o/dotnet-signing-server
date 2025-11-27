namespace DotNetSigningServer.Options;

public class LokiOptions
{
    public string? Url { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? BearerToken { get; set; }
    public string? App { get; set; } = "dotnet-signing-server";
}
