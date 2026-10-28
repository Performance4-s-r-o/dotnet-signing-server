namespace DotNetSigningServer.Options;

public class AiOptions
{
    /// <summary>
    /// Master switch for every /api/ai/* route. Off unless the raw value is exactly
    /// <c>true</c> (<see cref="IsOn"/>); anything else, empty or unreadable, is off.
    /// </summary>
    public bool Enabled { get; set; } = false;
    public string Provider { get; set; } = "google";
    public GoogleAiOptions Google { get; set; } = new();
}

public static class AiSwitch
{
    /// <summary>Fail-closed: only the exact text <c>true</c> turns AI on.</summary>
    public static bool IsOn(string? raw) => raw == "true";
}

public class GoogleAiOptions
{
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "gemini-2.5-flash";
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
}
