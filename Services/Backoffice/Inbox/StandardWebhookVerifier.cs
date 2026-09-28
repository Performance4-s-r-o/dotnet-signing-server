using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotNetSigningServer.Services.Backoffice.Inbox;

/// <summary>The request is not a valid webhook from the service.</summary>
public sealed class BackofficeWebhookVerificationException(string message) : Exception(message);

/// <summary>A verified webhook. <see cref="Id"/> is the dedupe key (= <c>id</c> in <c>GET /v1/events</c>).</summary>
public sealed record VerifiedWebhook(string Id, string Type, DateTimeOffset Timestamp, JsonElement Data);

/// <summary>
/// Standard Webhooks verification (https://www.standardwebhooks.com): HMAC-SHA256 over
/// <c>{webhook-id}.{webhook-timestamp}.{body}</c> with the base64 key of <c>whsec_…</c>,
/// constant-time compare, 5 minute timestamp tolerance. During a secret rotation either the
/// current or the previous secret may match.
///
/// Same algorithm and API shape as <c>P4.Backoffice.Sdk.Webhooks.WebhookVerifier</c>, kept
/// here because the default build leaves the SDK out (see <c>Directory.Build.props</c>); an
/// SDK build checks both produce the same signatures (<c>StandardWebhookVerifierTests</c>).
/// Switch to the SDK type once the SDK is part of every build.
/// </summary>
public sealed class StandardWebhookVerifier
{
    public const string SecretPrefix = "whsec_";
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";

    private readonly byte[][] _keys;
    private readonly TimeSpan _tolerance;

    /// <param name="secrets">Current secret, and the previous one during a rotation; empty ones are ignored.</param>
    /// <param name="tolerance">Allowed clock skew / delay (default 5 minutes).</param>
    public StandardWebhookVerifier(IEnumerable<string?> secrets, TimeSpan? tolerance = null)
    {
        _keys = secrets.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => DecodeSecret(s!)).ToArray();
        if (_keys.Length == 0) throw new ArgumentException("At least one webhook secret is required.", nameof(secrets));
        _tolerance = tolerance ?? DefaultTolerance;
    }

    private static byte[] DecodeSecret(string secret)
    {
        var trimmed = secret.Trim();
        var raw = trimmed.StartsWith(SecretPrefix, StringComparison.Ordinal) ? trimmed[SecretPrefix.Length..] : trimmed;
        try
        {
            return Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            throw new ArgumentException("The webhook secret is not valid base64 (whsec_…).");
        }
    }

    /// <summary>Signature for the given parts (<c>v1,&lt;base64&gt;</c>), e.g. for tests.</summary>
    public static string Sign(string secret, string id, long timestamp, string body)
    {
        var key = DecodeSecret(secret);
        return "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));
    }

    /// <summary>Verifies the three headers and the raw body (exactly as received).</summary>
    /// <exception cref="BackofficeWebhookVerificationException">Missing headers, stale timestamp, no matching signature or an unreadable payload.</exception>
    public VerifiedWebhook Verify(string body, string? id, string? timestamp, string? signature, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature))
            throw new BackofficeWebhookVerificationException("Missing webhook-id, webhook-timestamp or webhook-signature header.");
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var ts))
            throw new BackofficeWebhookVerificationException("Invalid webhook-timestamp.");
        if (Math.Abs((double)now.ToUnixTimeSeconds() - ts) > _tolerance.TotalSeconds)
            throw new BackofficeWebhookVerificationException("Timestamp outside the tolerance.");

        var content = Encoding.UTF8.GetBytes($"{id}.{ts}.{body}");
        var expected = _keys.Select(k => HMACSHA256.HashData(k, content)).ToArray();
        var ok = false;
        foreach (var candidate in signature.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var comma = candidate.IndexOf(',');
            if (comma < 0 || candidate[..comma] != "v1") continue;
            byte[] got;
            try { got = Convert.FromBase64String(candidate[(comma + 1)..]); }
            catch (FormatException) { continue; }
            foreach (var e in expected) ok |= CryptographicOperations.FixedTimeEquals(got, e);
        }
        if (!ok) throw new BackofficeWebhookVerificationException("No matching signature.");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(type.GetString()))
            {
                throw new BackofficeWebhookVerificationException("The payload has no type.");
            }

            var at = root.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String
                     && DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.FromUnixTimeSeconds(ts);
            var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
            return new VerifiedWebhook(id, type.GetString()!, at, data);
        }
        catch (JsonException)
        {
            throw new BackofficeWebhookVerificationException("The payload is not JSON.");
        }
    }

    /// <summary>Reads the three headers from a lookup (e.g. <c>Request.Headers</c>).</summary>
    public VerifiedWebhook Verify(string body, Func<string, string?> header, DateTimeOffset now) =>
        Verify(body, header(IdHeader), header(TimestampHeader), header(SignatureHeader), now);
}
