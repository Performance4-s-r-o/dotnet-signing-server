namespace DotNetSigningServer.Options
{
    public class TimestampAuthorityOptions
    {
        public string? Url { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(Url);
    }
}
