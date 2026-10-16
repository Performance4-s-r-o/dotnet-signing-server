using System.Globalization;

namespace DotNetSigningServer.Options;

/// <summary>
/// Raw environment values from docker-compose (#80). Compose falls back to a Coolify project
/// variable (<c>${NAME:-{{project.DOTNET_NAME}}}</c>); when that shared variable does not exist
/// the placeholder may arrive as text. Empty or a placeholder = unset (the code default);
/// anything else that does not parse fails startup instead of silently becoming the default.
/// </summary>
public static class EnvValues
{
    public static bool IsUnset(string? raw) =>
        string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith("{{", StringComparison.Ordinal);

    public static int? Int(IConfiguration configuration, string name)
    {
        var raw = configuration[name];
        if (IsUnset(raw)) return null;
        return int.TryParse(raw!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException($"{name} must be a whole number, got '{raw}'.");
    }

    public static DateTimeOffset? Instant(IConfiguration configuration, string name)
    {
        var raw = configuration[name];
        if (IsUnset(raw)) return null;
        return DateTimeOffset.TryParse(raw!.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToUniversalTime()
            : throw new InvalidOperationException($"{name} must be an ISO 8601 instant such as 2026-10-20T12:00:00Z, got '{raw}'.");
    }
}
