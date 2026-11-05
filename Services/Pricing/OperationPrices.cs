using System.Text.Json;

namespace DotNetSigningServer.Services.Pricing;

/// <summary>An operation this product charges credits for, as sent in the operations manifest.</summary>
/// <param name="Key">Key in the price list's <c>credit_costs</c>.</param>
/// <param name="Unit">For people only; the product counts the units itself.</param>
/// <param name="DefaultCredits">What is charged per unit without a price (informative in the manifest).</param>
/// <param name="RequiresPrice">Without a price in the price list the operation is not offered at all.</param>
public sealed record PricedOperation(string Key, string Label, string Description, string Unit, int DefaultCredits, bool RequiresPrice);

/// <summary>
/// Every operation the code charges, and which API operation (<c>RunOperationAsync</c> name) charges
/// which key. A test checks that every charged API operation is listed here, so nothing is charged
/// without being in the manifest.
/// </summary>
public static class PricedOperations
{
    public const string Sign = "sign";
    public const string SignPfx = "sign_pfx";
    public const string Timestamp = "timestamp";
    public const string VisualSign = "visual_sign";
    public const string ExtendSignature = "extend_signature";
    public const string Attachment = "attachment";
    public const string PdfaConvert = "pdfa_convert";
    public const string FlattenForm = "flatten_form";
    public const string FillPdf = "fill_pdf";
    public const string FindCodes = "find_codes";
    public const string AiDetectFields = "ai_detect_fields";
    public const string AiExtract = "ai_extract";

    /// <summary>Credits per unit when the price list has no price (and the operation does not require one).</summary>
    public const int MissingPriceCredits = 1;

    public static readonly IReadOnlyList<PricedOperation> Manifest =
    [
        new(Sign, "Signature", "Completing a signature (/api/sign), plus the preparation of its presign.", "operation", 1, false),
        new(SignPfx, "Signature with a PFX certificate", "/api/sign-pfx.", "operation", 1, false),
        new(Timestamp, "Timestamp", "/api/timestamp, plus its document preparation.", "operation", 1, false),
        new(VisualSign, "Visual signature", "/api/visual-sign, plus its document preparation.", "operation", 1, false),
        new(ExtendSignature, "Signature extension (LTV)", "/api/extend-signature.", "operation", 1, false),
        new(Attachment, "Attachment", "/api/attachment.", "operation", 1, false),
        new(PdfaConvert, "PDF/A conversion", "Also when a document is prepared for signing. Per 3 pages.", "3 pages", 1, false),
        new(FlattenForm, "Form flattening", "Also when a document is prepared for signing.", "operation", 1, false),
        new(FillPdf, "Template fill", "Per 3 pages of each generated document.", "3 pages", 1, false),
        new(FindCodes, "Code search", "Per 3 scanned pages.", "3 pages", 1, false),
        new(AiDetectFields, "AI field detection", "/api/ai/detect-fields.", "operation", 2, true),
        new(AiExtract, "AI data extraction", "/api/ai/extract-data.", "operation", 2, true),
    ];

    /// <summary>The priced key behind each charged API operation (the name passed to <c>RunOperationAsync</c>).</summary>
    public static readonly IReadOnlyDictionary<string, string> ByApiOperation = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sign"] = Sign,
        ["sign-pfx"] = SignPfx,
        ["timestamp"] = Timestamp,
        ["visual-sign"] = VisualSign,
        ["extend-signature"] = ExtendSignature,
        ["attachment"] = Attachment,
        ["pdfa"] = PdfaConvert,
        ["flatten-form"] = FlattenForm,
        ["fill-pdf"] = FillPdf,
        ["find-codes"] = FindCodes,
        ["ai-detect-fields"] = AiDetectFields,
        ["ai-extract-data"] = AiExtract,
    };

    private static readonly Dictionary<string, PricedOperation> ByKey = Manifest.ToDictionary(o => o.Key, StringComparer.Ordinal);

    public static PricedOperation Get(string key) =>
        ByKey.TryGetValue(key, out var operation)
            ? operation
            : throw new ArgumentOutOfRangeException(nameof(key), key, "Operation is not in the manifest");
}

/// <summary>An operation the price list leaves without a price although it requires one: it is not offered.</summary>
public sealed class OperationNotOfferedException(string key)
    : Exception($"Operation {key} has no price in the price list and is not offered")
{
    public const string Code = "OPERATION_NOT_OFFERED";

    public string Key { get; } = key;
}

/// <summary>
/// Credits of an operation from the price list in force (<c>credit_costs</c> of the snapshot of
/// <c>GET /v1/pricing/current</c>; the last known snapshot during an outage). A missing price is
/// <see cref="PricedOperations.MissingPriceCredits"/>; a missing price of an operation that requires one
/// means it is not offered. <c>0</c> is a price (free), never "missing". Without a price list (Pricing
/// Off, on-prem) every price is missing.
/// </summary>
public interface IOperationPrices
{
    /// <summary>Credits per unit; null when the operation is not offered.</summary>
    int? PerUnit(string key);
}

public static class OperationPricesExtensions
{
    /// <summary>Credits for <paramref name="units"/> units; throws <see cref="OperationNotOfferedException"/> when not offered.</summary>
    public static int Credits(this IOperationPrices prices, string key, int units = 1)
    {
        var perUnit = prices.PerUnit(key) ?? throw new OperationNotOfferedException(key);
        return checked(perUnit * Math.Max(0, units));
    }

    public static bool IsOffered(this IOperationPrices prices, string key) => prices.PerUnit(key) is not null;
}

public sealed class OperationPrices : IOperationPrices
{
    private readonly PricingSnapshotHolder? _holder;
    private readonly ILogger<OperationPrices> _logger;
    private readonly object _lock = new();
    private (PricingSnapshot? Snapshot, IReadOnlyDictionary<string, int> Costs)? _resolved;

    /// <param name="holder">The price-list snapshot; null with Pricing Off.</param>
    public OperationPrices(PricingSnapshotHolder? holder, ILogger<OperationPrices> logger)
    {
        _holder = holder;
        _logger = logger;
    }

    public int? PerUnit(string key)
    {
        var operation = PricedOperations.Get(key);
        if (Costs().TryGetValue(key, out var credits)) return credits;
        return operation.RequiresPrice ? null : PricedOperations.MissingPriceCredits;
    }

    private IReadOnlyDictionary<string, int> Costs()
    {
        var snapshot = _holder?.Current;
        var resolved = _resolved;
        if (resolved is { } r && ReferenceEquals(r.Snapshot, snapshot)) return r.Costs;
        lock (_lock)
        {
            resolved = _resolved;
            if (resolved is { } again && ReferenceEquals(again.Snapshot, snapshot)) return again.Costs;
            var costs = Parse(snapshot);
            _resolved = (snapshot, costs);
            return costs;
        }
    }

    private IReadOnlyDictionary<string, int> Parse(PricingSnapshot? snapshot)
    {
        var costs = new Dictionary<string, int>(StringComparer.Ordinal);
        if (snapshot is null) return costs;
        if (snapshot.Body.ValueKind != JsonValueKind.Object
            || !snapshot.Body.TryGetProperty("credit_costs", out var raw)
            || raw.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("[pricing] price list v{Version} has no credit_costs; every operation is charged {Credits} credit, AI is not offered",
                snapshot.Version, PricedOperations.MissingPriceCredits);
            return costs;
        }
        foreach (var property in raw.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var credits) && credits >= 0)
            {
                costs[property.Name] = credits;
            }
            else
            {
                _logger.LogWarning("[pricing] credit_costs.{Key} of price list v{Version} is not a whole number ≥ 0; treated as missing",
                    property.Name, snapshot.Version);
            }
        }
        var missing = PricedOperations.Manifest.Where(o => !costs.ContainsKey(o.Key)).Select(o => o.Key).ToList();
        if (missing.Count > 0)
        {
            _logger.LogWarning("[pricing] price list v{Version} has no price for {Keys}; those cost {Credits} credit, or are not offered when they require a price",
                snapshot.Version, string.Join(", ", missing), PricedOperations.MissingPriceCredits);
        }
        return costs;
    }
}

/// <summary>
/// Sends the operations manifest once at start (<c>PUT /v1/pricing/operations</c>, a full replace,
/// no idempotency key): every operation the code charges, with what it charges without a price.
/// A failure is logged and does not stop the start; the next start sends it again.
/// </summary>
public sealed class OperationsManifestPublisher : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly IHttpClientFactory _httpClients;
    private readonly ILogger<OperationsManifestPublisher> _logger;

    public OperationsManifestPublisher(IHttpClientFactory httpClients, ILogger<OperationsManifestPublisher> logger)
    {
        _httpClients = httpClients;
        _logger = logger;
    }

    /// <summary>The body of the manifest request.</summary>
    public static string Body(string codeVersion) => JsonSerializer.Serialize(new
    {
        CodeVersion = codeVersion,
        Operations = PricedOperations.Manifest.Select(o => new
        {
            o.Key,
            o.Label,
            o.Description,
            o.Unit,
            o.DefaultCredits,
            o.RequiresPrice,
        }),
    }, Json);

    /// <summary>Version of this build, as the service shows it next to the manifest.</summary>
    public static string CodeVersion =>
        typeof(OperationsManifestPublisher).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion is { Length: > 0 } version ? version : "unknown";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "v1/pricing/operations")
            {
                Content = new StringContent(Body(CodeVersion), System.Text.Encoding.UTF8, "application/json"),
            };
            using var response = await _httpClients.CreateClient(BackofficePricingClient.HttpClientName).SendAsync(request, stoppingToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[pricing] the operations manifest was refused ({Status}); sent again at the next start", (int)response.StatusCode);
                return;
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(stoppingToken));
            var root = document.RootElement;
            foreach (var field in new[] { "missing_in_price_list", "undefined_operations" })
            {
                if (root.TryGetProperty(field, out var keys) && keys.ValueKind == JsonValueKind.Array && keys.GetArrayLength() > 0)
                {
                    _logger.LogWarning("[pricing] operations manifest: {Field} = {Keys}", field,
                        string.Join(", ", keys.EnumerateArray().Select(k => k.ToString())));
                }
            }
            _logger.LogInformation("[pricing] operations manifest sent ({Count} operations)", PricedOperations.Manifest.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning("[pricing] sending the operations manifest failed ({Error}); sent again at the next start", ex.GetType().Name);
        }
    }
}
