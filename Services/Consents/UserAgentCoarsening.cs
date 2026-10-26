using System.Text.RegularExpressions;

namespace DotNetSigningServer.Services.Consents;

/// <summary>Reduces a user agent to browser family, major version and OS family before it leaves for the backoffice service.</summary>
public static partial class UserAgentCoarsening
{
    // Order matters: Edge and Opera also say Chrome, Chrome also says Safari.
    private static readonly (string Name, Regex Pattern)[] Browsers =
    [
        ("Edge", EdgeRx()),
        ("Opera", OperaRx()),
        ("Firefox", FirefoxRx()),
        ("Chrome", ChromeRx()),
        ("Safari", SafariRx()),
    ];

    /// <summary>"Chrome 120 / Windows", or just one part when the other is unknown. Null when neither is recognised.</summary>
    public static string? Coarsen(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return null;
        var ua = userAgent.Length > 1024 ? userAgent[..1024] : userAgent;

        string? browser = null;
        foreach (var (name, pattern) in Browsers)
        {
            var m = pattern.Match(ua);
            if (m.Success) { browser = $"{name} {m.Groups[1].Value}"; break; }
        }

        var os = OperatingSystem(ua);
        if (browser is null && os is null) return null;
        return browser is null ? os : os is null ? browser : $"{browser} / {os}";
    }

    private static string? OperatingSystem(string ua)
    {
        // iOS and Android first: their agents also contain "Mac OS X" and "Linux".
        if (ua.Contains("iPhone") || ua.Contains("iPad") || ua.Contains("iPod")) return "iOS";
        if (ua.Contains("Android")) return "Android";
        if (ua.Contains("Windows")) return "Windows";
        if (ua.Contains("CrOS")) return "ChromeOS";
        if (ua.Contains("Macintosh") || ua.Contains("Mac OS X")) return "macOS";
        if (ua.Contains("Linux") || ua.Contains("X11")) return "Linux";
        return null;
    }

    [GeneratedRegex(@"\bEdg(?:e|A|iOS)?/(\d+)")] private static partial Regex EdgeRx();
    [GeneratedRegex(@"\b(?:OPR|Opera)/(\d+)")] private static partial Regex OperaRx();
    [GeneratedRegex(@"\b(?:Firefox|FxiOS)/(\d+)")] private static partial Regex FirefoxRx();
    [GeneratedRegex(@"\b(?:Chrome|CriOS)/(\d+)")] private static partial Regex ChromeRx();
    [GeneratedRegex(@"\bVersion/(\d+)[\d.]*.*Safari/")] private static partial Regex SafariRx();
}
