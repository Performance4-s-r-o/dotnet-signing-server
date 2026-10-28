using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DotNetSigningServer.Options;

/// <summary>
/// Raw environment values from docker-compose (#80). Compose falls back to a Coolify project
/// variable (<c>${NAME:-{{project.DOTNET_NAME}}}</c>); when that shared variable does not exist
/// the placeholder may arrive as text. Empty or a placeholder = unset (the code default);
/// anything else that does not parse fails startup instead of silently becoming the default.
/// </summary>
public static class EnvValues
{
    // A value that is only a placeholder: {{project.X}} / {{environment.X}}, optional spaces.
    private static readonly Regex Placeholder = new(@"^\s*\{\{\s*[A-Za-z_]+\.[A-Za-z0-9_]+\s*\}\}\s*$", RegexOptions.CultureInvariant);

    public static bool IsUnresolvedPlaceholder(string? raw) => raw is not null && Placeholder.IsMatch(raw);

    /// <summary>
    /// Removes every environment variable whose whole value is an unresolved Coolify
    /// placeholder, before configuration is built. Values bound straight into options
    /// (<c>Backoffice__Outbox__GiveUpAfter</c>, <c>Cors__AllowedOrigins</c>…) never pass
    /// through <see cref="IsUnset"/>; without this a missing shared variable fails startup
    /// with "not a valid TimeSpan" instead of taking the default. Returns the removed names
    /// (names only — never log the values).
    /// </summary>
    public static IReadOnlyList<string> ScrubUnresolvedPlaceholders()
    {
        var removed = UnresolvedPlaceholderNames(Environment.GetEnvironmentVariables());
        foreach (var name in removed)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
        return removed;
    }

    /// <summary>Names whose whole value is an unresolved placeholder, sorted; pure.</summary>
    public static IReadOnlyList<string> UnresolvedPlaceholderNames(IDictionary variables)
    {
        var names = new List<string>();
        foreach (DictionaryEntry entry in variables)
        {
            if (entry.Key is string name && IsUnresolvedPlaceholder(entry.Value as string))
            {
                names.Add(name);
            }
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

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
}
