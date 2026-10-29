using System.Security.Cryptography;
using System.Text;
using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Middleware;

/// <summary>How the policy is applied (<c>CSP_MODE</c>).</summary>
public enum CspMode
{
    Enforce,
    ReportOnly,
    Off,
}

public sealed class CspOptions
{
    public const string SectionName = "Csp";

    /// <summary><c>report-only</c> (default), <c>enforce</c> or <c>off</c>.</summary>
    public string? Mode { get; set; }

    public CspMode EffectiveMode => (Mode ?? "").Trim().ToLowerInvariant() switch
    {
        "enforce" => CspMode.Enforce,
        "" or "report-only" or "reportonly" => CspMode.ReportOnly,
        "off" => CspMode.Off,
        _ => throw new InvalidOperationException(
            "Csp:Mode (environment variable Csp__Mode / CSP_MODE) must be enforce, report-only or off."),
    };
}

/// <summary>
/// Content-Security-Policy for HTML pages: scripts only with the request's nonce (no inline
/// scripts without it, no inline event handlers), no plugins, no framing of this site. Every
/// <c>&lt;script&gt;</c> in a view gets the nonce from <see cref="TagHelpers.CspNonceTagHelper"/>.
/// Styles keep <c>'unsafe-inline'</c> (the views use style attributes). The template builder
/// compiles its JSX in the browser and sets <see cref="AllowEvalKey"/>.
/// </summary>
public sealed class ContentSecurityPolicyMiddleware
{
    public const string NonceKey = "csp-nonce";
    public const string AllowEvalKey = "csp-allow-eval";
    private const string PdfJsHost = "https://cdnjs.cloudflare.com";

    private readonly RequestDelegate _next;
    private readonly CspMode _mode;
    private readonly IReadOnlyList<string> _widgetOrigins;
    private readonly string? _frameOrigin;

    public ContentSecurityPolicyMiddleware(RequestDelegate next, IOptions<CspOptions> csp, IOptions<P4BackofficeProductOptions> backoffice)
    {
        _next = next;
        _mode = csp.Value.EffectiveMode;
        var b = backoffice.Value;
        var active = b.DisabledReason == Services.Backoffice.BackofficeDisabledReason.None;
        var origins = new List<string>();
        if (active)
        {
            AddOrigin(origins, b.CookieWidget.ScriptSrc);
            _frameOrigin = Origin(b.SupportForm.ScriptSrc);
            if (_frameOrigin != null) origins.Add(_frameOrigin);
        }
        _widgetOrigins = origins.Distinct().ToList();
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (_mode == CspMode.Off) return _next(context);

        context.Response.OnStarting(() =>
        {
            var type = context.Response.ContentType;
            if (type == null || !type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
            var policy = Build(
                GetNonce(context),
                context.Items.ContainsKey(AllowEvalKey),
                _widgetOrigins,
                _frameOrigin);
            var header = _mode == CspMode.Enforce ? "Content-Security-Policy" : "Content-Security-Policy-Report-Only";
            context.Response.Headers[header] = policy;
            return Task.CompletedTask;
        });
        return _next(context);
    }

    /// <summary>The request's nonce, created on first use.</summary>
    public static string GetNonce(HttpContext context)
    {
        if (context.Items[NonceKey] is string existing) return existing;
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        context.Items[NonceKey] = nonce;
        return nonce;
    }

    /// <summary>The policy text. Pure.</summary>
    public static string Build(string nonce, bool allowEval, IReadOnlyList<string> extraOrigins, string? frameOrigin)
    {
        var extra = extraOrigins.Count == 0 ? "" : " " + string.Join(' ', extraOrigins);
        var sb = new StringBuilder();
        sb.Append("default-src 'self'; ");
        // strict-dynamic: scripts the nonce'd ones load (the cookie widget's parts) run too.
        sb.Append($"script-src 'nonce-{nonce}' 'strict-dynamic'{(allowEval ? " 'unsafe-eval'" : "")}; ");
        sb.Append("style-src 'self' 'unsafe-inline'; ");
        sb.Append($"img-src 'self' data: blob:{extra}; ");
        sb.Append("font-src 'self' data:; ");
        sb.Append($"connect-src 'self'{extra}; ");
        sb.Append($"frame-src 'self'{(frameOrigin == null ? "" : " " + frameOrigin)}; ");
        sb.Append($"worker-src 'self' blob:{(allowEval ? " " + PdfJsHost : "")}; ");
        sb.Append("object-src 'none'; base-uri 'self'; frame-ancestors 'none'");
        // No form-action: a payment form posts to us and is redirected to the hosted checkout,
        // which Chromium counts as a form target.
        return sb.ToString();
    }

    private static void AddOrigin(List<string> list, string? url)
    {
        if (Origin(url) is { } o) list.Add(o);
    }

    private static string? Origin(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
}
