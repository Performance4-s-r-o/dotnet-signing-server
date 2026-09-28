using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using DotNetSigningServer.Services.Backoffice.Outbox;

namespace DotNetSigningServer.Services.Email;

/// <summary>
/// <c>Modules:Email=Shadow</c> for templates listed in <c>P4Backoffice:Email:TemplateKeys</c>:
/// the message is sent locally as with Off, and in the background the service renders the same
/// template (<c>POST /v1/templates/{key}/render</c>, nothing is sent) and differences in the
/// subject and the text (whitespace normalised) are logged.
///
/// Never on the request path: <see cref="Schedule"/> only writes to a bounded queue (dropped
/// when full). Variable values are replaced by their <c>{{name}}</c> before comparing and
/// logging, so codes and links never reach the log. A key without the <c>templates:read</c>
/// scope (401/403) turns the comparison off until restart.
/// </summary>
public sealed partial class TemplateShadowComparer : BackgroundService
{
    public const string HttpClientName = "P4Backoffice.Templates";

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public const int Capacity = 100;

    private readonly Channel<TemplateShadowRequest> _queue = Channel.CreateBounded<TemplateShadowRequest>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly IHttpClientFactory _http;
    private readonly ILogger<TemplateShadowComparer> _logger;
    private volatile bool _disabled;

    public TemplateShadowComparer(IHttpClientFactory http, ILogger<TemplateShadowComparer> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>True once the service refused the key (missing <c>templates:read</c>).</summary>
    public bool Disabled => _disabled;

    /// <summary>Queues a comparison. Never blocks, never throws.</summary>
    public void Schedule(
        string templateKey, string serviceLocale, IReadOnlyDictionary<string, string?> variables, EmailTemplateResult local)
    {
        if (_disabled) return;
        var copy = variables.ToDictionary(v => v.Key, v => v.Value ?? "", StringComparer.Ordinal);
        _queue.Writer.TryWrite(new TemplateShadowRequest(templateKey, serviceLocale, copy, local.Subject, local.HtmlBody));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await CompareAsync(request, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>One comparison; logs and swallows every failure.</summary>
    internal async Task CompareAsync(TemplateShadowRequest request, CancellationToken cancellationToken)
    {
        if (_disabled) return;
        try
        {
            var client = _http.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(
                $"v1/templates/{Uri.EscapeDataString(request.TemplateKey)}/render",
                new { locale = request.Locale, variables = request.Variables },
                BackofficeOutbox.PayloadJson,
                cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _disabled = true;
                _logger.LogWarning(
                    "[email-shadow] the service refused template rendering ({Status}); the key needs the templates:read scope. "
                    + "Template comparison is off until restart",
                    (int)response.StatusCode);
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                var code = await ProblemCodeAsync(response, cancellationToken);
                _logger.LogWarning("[email-shadow] {TemplateKey}/{Locale}: the service did not render it ({Status} {Code})",
                    request.TemplateKey, request.Locale, (int)response.StatusCode, code);
                return;
            }

            var remote = await response.Content.ReadFromJsonAsync<RenderedTemplate>(BackofficeOutbox.PayloadJson, cancellationToken);
            if (remote == null) return;

            var differences = TemplateShadowDiff.Describe(
                request.LocalSubject, request.LocalHtml, remote.Subject ?? "", remote.Text ?? "", request.Variables);
            if (differences.Count == 0)
            {
                _logger.LogInformation("[email-shadow] {TemplateKey}/{Locale}: local and service agree",
                    request.TemplateKey, request.Locale);
            }
            else
            {
                _logger.LogWarning("[email-shadow] {TemplateKey}/{Locale} (service v{Version}) differs: {Differences}",
                    request.TemplateKey, request.Locale, remote.Version, string.Join("; ", differences));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("[email-shadow] {TemplateKey}/{Locale}: comparison failed ({Error})",
                request.TemplateKey, request.Locale, ex.GetType().Name);
        }
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("code", out var code)
                   && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class RenderedTemplate
    {
        public int Version { get; init; }
        public string? Subject { get; init; }
        public string? Text { get; init; }
    }
}

/// <summary>One queued comparison.</summary>
public sealed record TemplateShadowRequest(
    string TemplateKey,
    string Locale,
    IReadOnlyDictionary<string, string> Variables,
    string LocalSubject,
    string LocalHtml);

/// <summary>What the template shadow mode compares. Pure.</summary>
public static partial class TemplateShadowDiff
{
    /// <summary>Characters shown around the first difference.</summary>
    public const int Context = 40;

    /// <summary>Differences in subject and text (variables masked, whitespace normalised); empty when they agree.</summary>
    public static IReadOnlyList<string> Describe(
        string localSubject, string localHtml, string serviceSubject, string serviceText,
        IReadOnlyDictionary<string, string> variables)
    {
        var differences = new List<string>();
        var subject = FirstDifference(
            Normalize(localSubject, variables), Normalize(serviceSubject, variables));
        if (subject != null) differences.Add("subject " + subject);
        var text = FirstDifference(
            Normalize(HtmlToText(localHtml), variables), Normalize(serviceText, variables));
        if (text != null) differences.Add("text " + text);
        return differences;
    }

    /// <summary>Masks variable values as <c>{{name}}</c> (longest first) and collapses whitespace.</summary>
    public static string Normalize(string value, IReadOnlyDictionary<string, string> variables)
    {
        var result = value ?? "";
        foreach (var (name, raw) in variables
                     .Where(v => !string.IsNullOrWhiteSpace(v.Value))
                     .OrderByDescending(v => v.Value.Length))
        {
            result = result.Replace(raw, "{{" + name + "}}", StringComparison.Ordinal);
        }
        return Whitespace().Replace(result, " ").Trim();
    }

    /// <summary>Visible text of an HTML e-mail (no head, styles or tags; entities decoded).</summary>
    public static string HtmlToText(string html)
    {
        var text = Comments().Replace(html ?? "", " ");
        text = HiddenBlocks().Replace(text, " ");
        text = LineBreaks().Replace(text, "\n");
        text = Tags().Replace(text, " ");
        return WebUtility.HtmlDecode(text);
    }

    /// <summary><c>at N: local="…" service="…"</c> around the first differing character, or null.</summary>
    public static string? FirstDifference(string local, string service)
    {
        if (string.Equals(local, service, StringComparison.Ordinal)) return null;
        var i = 0;
        while (i < local.Length && i < service.Length && local[i] == service[i]) i++;
        return $"at {i}: local=\"{Snippet(local, i)}\" service=\"{Snippet(service, i)}\"";
    }

    private static string Snippet(string value, int at)
    {
        var start = Math.Max(0, at - Context / 2);
        var length = Math.Min(Context, value.Length - start);
        return length <= 0 ? "" : value.Substring(start, length);
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<(head|style|script|title)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenBlocks();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|h[1-6]|li|tr|td|table)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
