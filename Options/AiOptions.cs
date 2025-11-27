namespace DotNetSigningServer.Options;

public class AiOptions
{
    public bool Enabled { get; set; } = false;
    public string Provider { get; set; } = "google";
    public GoogleAiOptions Google { get; set; } = new();
}

public class GoogleAiOptions
{
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "gemini-1.5-flash";
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
}
