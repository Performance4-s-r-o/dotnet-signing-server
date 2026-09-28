using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Consents;

namespace DotNetSigningServer.Services.Support;

/// <summary>What the form and the request contribute to a ticket (before normalisation).</summary>
public sealed record SupportTicketInput(
    string? Category,
    string? Subject,
    string? Message,
    string? Priority,
    string? Locale = null,
    string? Url = null,
    string? UserAgent = null,
    string? AppVersion = null);

/// <summary>
/// Body of <c>POST /v1/support/tickets</c> (<c>SupportTicketInput</c> of the service). Serialised
/// snake_case by the outbox; null members are left out, because <c>context</c> accepts no
/// unknown or null properties.
/// </summary>
public sealed record SupportTicketPayload(
    string Category,
    string Subject,
    string Message,
    SupportTicketReporter Reporter,
    SupportTicketContext Context,
    string Priority,
    string Source);

public sealed record SupportTicketReporter(string Email, string Name, string SubjectRef);

/// <summary>
/// Only what helps an agent reproduce the problem — no further personal data (no credits,
/// no IP, no e-mail: the address is in <see cref="SupportTicketReporter"/>).
/// </summary>
public sealed record SupportTicketContext(
    string Plan,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Locale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AppVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserAgent);

/// <summary>
/// Builds the service request of the in-app support form. Pure: no I/O, no clock.
///
/// The message is sent as <b>plain text</b>; the service escapes it before it reaches the
/// helpdesk, so nothing here builds or encodes HTML.
/// </summary>
public static partial class SupportTicketRequest
{
    public const string OutboxKind = "support.ticket";

    public const int MaxSubjectLength = 200;
    public const int MaxMessageLength = 8000;
    public const int MaxNameLength = 200;
    public const int MaxEmailLength = 320;
    public const int MaxUrlLength = 2000;
    public const int MaxAppVersionLength = 50;
    public const int MaxUserAgentLength = 500;

    public const string Source = "in_app";
    public const string DefaultPriority = "normal";
    public const string FallbackCategory = "other";

    public const string PlanEnterprise = "enterprise";
    public const string PlanStandard = "standard";

    public static readonly IReadOnlyList<string> Priorities = ["low", "normal", "high"];

    /// <summary>
    /// Categories of the form when the service's list is not available (the helpdesk topics
    /// the product has always used). The service's import uses the same keys.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultCategories = ["signing", "templates", "billing", "account", "other"];

    /// <summary>
    /// The request for <paramref name="user"/>. <paramref name="categories"/> are the keys the
    /// form offered; an unknown category becomes <see cref="FallbackCategory"/> (or the first
    /// offered key when that is not among them).
    /// </summary>
    public static SupportTicketPayload Build(User user, SupportTicketInput input, IReadOnlyCollection<string>? categories = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(input);

        var email = Truncate(user.Email.Trim(), MaxEmailLength);
        return new SupportTicketPayload(
            Category: NormalizeCategory(input.Category, categories ?? DefaultCategories),
            Subject: NormalizeSubject(input.Subject),
            Message: NormalizeMessage(input.Message),
            Reporter: new SupportTicketReporter(
                Email: email,
                Name: Truncate(ReporterName(email), MaxNameLength),
                SubjectRef: ConsentRequirements.SubjectRef(user.Id)),
            Context: new SupportTicketContext(
                Plan: user.IsEnterprise ? PlanEnterprise : PlanStandard,
                Locale: NormalizeLocale(input.Locale),
                Url: NormalizeUrl(input.Url),
                AppVersion: Optional(input.AppVersion, MaxAppVersionLength),
                UserAgent: Optional(input.UserAgent, MaxUserAgentLength)),
            Priority: NormalizePriority(input.Priority),
            Source: Source);
    }

    /// <summary>
    /// Name shown to support agents. Accounts have no display name, so it is the e-mail
    /// address; never empty (the service requires one).
    /// </summary>
    public static string ReporterName(string? email) =>
        string.IsNullOrWhiteSpace(email) ? "User" : email.Trim();

    /// <summary>One line, trimmed, at most <see cref="MaxSubjectLength"/> characters.</summary>
    public static string NormalizeSubject(string? subject)
    {
        var line = WhitespaceRun().Replace(subject ?? "", " ").Trim();
        return Truncate(line, MaxSubjectLength);
    }

    /// <summary>Plain text with <c>\n</c> line ends, trimmed, at most <see cref="MaxMessageLength"/> characters.</summary>
    public static string NormalizeMessage(string? message)
    {
        var text = (message ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return Truncate(text, MaxMessageLength);
    }

    public static string NormalizePriority(string? priority)
    {
        var value = priority?.Trim().ToLowerInvariant();
        return value != null && Priorities.Contains(value) ? value : DefaultPriority;
    }

    public static string NormalizeCategory(string? category, IReadOnlyCollection<string> offered)
    {
        var value = category?.Trim();
        if (value != null && offered.Contains(value) && IsCategoryKey(value)) return value;
        if (offered.Contains(FallbackCategory) || offered.Count == 0) return FallbackCategory;
        return offered.First();
    }

    /// <summary>A key the service accepts as <c>category</c> (<c>^[a-z][a-z0-9_]{0,39}$</c>).</summary>
    public static bool IsCategoryKey(string? key) => !string.IsNullOrEmpty(key) && CategoryPattern().IsMatch(key);

    /// <summary><c>cs</c>, <c>en-US</c>, …; anything else is left out.</summary>
    public static string? NormalizeLocale(string? locale)
    {
        var value = locale?.Trim();
        return !string.IsNullOrEmpty(value) && LocalePattern().IsMatch(value) ? value : null;
    }

    /// <summary>An absolute http(s) URL without query or fragment (they may carry tokens); otherwise left out.</summary>
    public static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        var clean = uri.GetLeftPart(UriPartial.Path);
        return clean.Length <= MaxUrlLength ? clean : null;
    }

    /// <summary>The product's version (informational version without the build metadata).</summary>
    public static string? ProductVersion(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();
        if (version is null) return null;
        var plus = version.IndexOf('+');
        return Optional(plus > 0 ? version[..plus] : version, MaxAppVersionLength);
    }

    private static string? Optional(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : Truncate(trimmed, max);
    }

    /// <summary>Cuts at <paramref name="max"/> characters without splitting a surrogate pair.</summary>
    internal static string Truncate(string value, int max)
    {
        if (value.Length <= max) return value;
        var cut = max;
        if (char.IsHighSurrogate(value[cut - 1])) cut--;
        return value[..cut];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex CategoryPattern();

    [GeneratedRegex("^[a-z]{2}(-[A-Z]{2})?$")]
    private static partial Regex LocalePattern();
}
