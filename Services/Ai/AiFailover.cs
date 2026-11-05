namespace DotNetSigningServer.Services.Ai;

/// <summary>What went wrong with one provider call, as far as failover cares.</summary>
/// <param name="Transport"><c>timeout</c> or <c>network</c> when no answer arrived.</param>
public sealed record AiFailureInput(
    string Provider,
    int? HttpStatus = null,
    string? ErrorType = null,
    string? Message = null,
    string? StopReason = null,
    bool SchemaInvalid = false,
    string? Transport = null);

/// <summary>Failover decision: switch to the next provider (and report) or stop.</summary>
/// <param name="Report">Kind for <c>POST /v1/ai/provider-events</c>; null = nothing to report.</param>
public sealed record AiFailoverDecision(bool Switch, string? Report, string Rule);

/// <summary>
/// The failover rules of the AI gateway contract: switch only on exhaustion and outages (credit,
/// rate limit, overload, 5xx, timeout, network), never on another 4xx, a refusal or output that does
/// not match the schema — otherwise every provider would get the same document.
/// </summary>
public static class AiFailover
{
    public const string CreditExhausted = "credit_exhausted";
    public const string RateLimited = "rate_limited";
    public const string UnavailableReport = "unavailable";

    private static AiFailoverDecision Switch(string report, string rule) => new(true, report, rule);

    private static AiFailoverDecision Stop(string rule) => new(false, null, rule);

    public static AiFailoverDecision Classify(AiFailureInput input)
    {
        switch (input.Transport)
        {
            case "timeout": return Switch(UnavailableReport, "timeout");
            case "network": return Switch(UnavailableReport, "network");
        }

        var status = input.HttpStatus ?? 0;
        if (status is >= 200 and < 300)
        {
            if (IsRefusal(input.Provider, input.StopReason)) return Stop("refusal");
            return Stop("schema_invalid");
        }

        if (input.Provider == "anthropic")
        {
            if (status == 402 || input.ErrorType == "billing_error"
                || (status == 400 && input.Message?.Contains("credit balance is too low", StringComparison.OrdinalIgnoreCase) == true))
            {
                return Switch(CreditExhausted, "credit");
            }
            if (status == 429 || input.ErrorType == "rate_limit_error") return Switch(RateLimited, "rate_limit");
            if (status == 529 || input.ErrorType == "overloaded_error") return Switch(UnavailableReport, "overloaded");
        }
        else if (status == 429 || input.ErrorType == "RESOURCE_EXHAUSTED")
        {
            return Switch(RateLimited, "rate_limit");
        }

        if (status >= 500) return Switch(UnavailableReport, "server_error");
        return Stop("client_error");
    }

    /// <summary>A 200 that declines: Anthropic <c>refusal</c>, Gemini safety and other blocks.</summary>
    public static bool IsRefusal(string provider, string? stopReason) => provider switch
    {
        "anthropic" => stopReason == "refusal",
        _ => stopReason is "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "IMAGE_SAFETY",
    };
}
