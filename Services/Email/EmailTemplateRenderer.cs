using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;

namespace DotNetSigningServer.Services.Email;

/// <summary>
/// Reads static HTML files from Resources/EmailTemplates/{locale}/{template}.html,
/// substitutes {{variable}} placeholders, and wraps the result in a shared layout
/// that keeps branding consistent across every outbound email.
///
/// Each template file must start with an HTML comment of the form
/// <!-- subject: ... -->
/// on its first line. The comment is stripped from the body.
/// </summary>
public class EmailTemplateRenderer : IEmailTemplateRenderer
{
    private const string DefaultLocale = "en";
    private static readonly Regex PlaceholderRegex = new(@"\{\{(?<name>[a-zA-Z0-9_]+)\}\}", RegexOptions.Compiled);
    private static readonly Regex SubjectRegex = new(@"^\s*<!--\s*subject:\s*(?<subject>.*?)\s*-->\s*", RegexOptions.Compiled);

    private readonly string _templatesRoot;
    private readonly ConcurrentDictionary<string, CachedTemplate> _cache = new(StringComparer.Ordinal);
    private readonly ILogger<EmailTemplateRenderer> _logger;
    private readonly Lazy<string> _layout;

    public EmailTemplateRenderer(ILogger<EmailTemplateRenderer> logger, IHostEnvironment environment)
    {
        _logger = logger;
        _templatesRoot = ResolveTemplatesRoot(environment);
        _layout = new Lazy<string>(() => ReadFile(Path.Combine(_templatesRoot, "_layout.html")));
    }

    public EmailTemplateResult Render(string templateId, string locale, IReadOnlyDictionary<string, string?>? variables)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            throw new ArgumentException("Template id is required.", nameof(templateId));

        var template = Load(templateId, NormalizeLocale(locale));
        var vars = BuildVariableSet(variables, locale);

        var renderedBody = Substitute(template.Body, vars);
        var subject = Substitute(template.Subject, vars);

        vars["body"] = renderedBody;
        var html = Substitute(_layout.Value, vars);
        return new EmailTemplateResult(subject, html);
    }

    private CachedTemplate Load(string templateId, string locale)
    {
        var cacheKey = $"{locale}:{templateId}";
        return _cache.GetOrAdd(cacheKey, _ =>
        {
            var path = FindTemplateFile(templateId, locale);
            var raw = ReadFile(path);
            var match = SubjectRegex.Match(raw);
            if (!match.Success)
            {
                throw new InvalidOperationException(
                    $"Template {path} is missing the subject header. First line must be <!-- subject: ... -->.");
            }
            var subject = match.Groups["subject"].Value;
            var body = raw.Substring(match.Length);
            return new CachedTemplate(subject, body);
        });
    }

    private string FindTemplateFile(string templateId, string locale)
    {
        var primary = Path.Combine(_templatesRoot, locale, templateId + ".html");
        if (File.Exists(primary)) return primary;

        if (!string.Equals(locale, DefaultLocale, StringComparison.OrdinalIgnoreCase))
        {
            var fallback = Path.Combine(_templatesRoot, DefaultLocale, templateId + ".html");
            if (File.Exists(fallback))
            {
                _logger.LogWarning(
                    "Email template {TemplateId} missing for locale {Locale}, falling back to {Default}.",
                    templateId, locale, DefaultLocale);
                return fallback;
            }
        }

        throw new FileNotFoundException(
            $"Email template not found: {templateId} (locale {locale}). Expected at {primary}.");
    }

    private static string ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string NormalizeLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return DefaultLocale;
        var trimmed = locale.Trim().ToLowerInvariant();
        var dash = trimmed.IndexOf('-');
        return dash > 0 ? trimmed[..dash] : trimmed;
    }

    private static string Substitute(string template, IDictionary<string, string?> variables)
    {
        return PlaceholderRegex.Replace(template, m =>
        {
            var key = m.Groups["name"].Value;
            return variables.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
        });
    }

    private static Dictionary<string, string?> BuildVariableSet(
        IReadOnlyDictionary<string, string?>? input,
        string locale)
    {
        var normalized = NormalizeLocale(locale);
        var vars = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["locale"] = normalized,
            ["footer_signature"] = normalized == "cs" ? "Tým P4PDF" : "The P4PDF team",
            ["footer_company"] = normalized == "cs"
                ? "P4PDF provozuje Performance4 s.r.o."
                : "P4PDF is operated by Performance4 s.r.o.",
            ["footer_unsubscribe"] = normalized == "cs"
                ? "Nastavení emailů si můžete upravit v aplikaci."
                : "You can manage email preferences inside the app.",
            ["footer_support"] = normalized == "cs"
                ? "S dotazy se obraťte na support@performance4.cz."
                : "For questions contact support@performance4.cz.",
        };
        if (input == null) return vars;
        foreach (var kvp in input)
        {
            vars[kvp.Key] = kvp.Value;
        }
        return vars;
    }

    private static string ResolveTemplatesRoot(IHostEnvironment environment)
    {
        // Primary location: copied alongside the assembly via <Content Include> in csproj.
        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        var candidate = Path.Combine(assemblyDir, "Resources", "EmailTemplates");
        if (Directory.Exists(candidate)) return candidate;

        // Fallback: run from source tree (dotnet run / tests).
        candidate = Path.Combine(environment.ContentRootPath, "Resources", "EmailTemplates");
        if (Directory.Exists(candidate)) return candidate;

        throw new DirectoryNotFoundException(
            "Email templates directory not found. Expected at Resources/EmailTemplates/ in the content root.");
    }

    private sealed record CachedTemplate(string Subject, string Body);
}
