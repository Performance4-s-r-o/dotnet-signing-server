using System.Diagnostics;
using DotNetSigningServer.Data;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Settings;

namespace DotNetSigningServer.Services.Ai;

/// <summary>The provider that answered and its validated output.</summary>
public sealed record AiCallResult<T>(T Value, string Provider, string Model);

/// <summary>
/// Calls AI through the provider pool of the AI credentials endpoint: fill-first in the service's
/// order, at most <c>max_providers_per_call</c> providers, switching only by <see cref="AiFailover"/>.
/// A switch is reported (<c>POST /v1/ai/provider-events</c>) and every answer's usage recorded
/// (<c>POST /v1/ai/usage</c>), both through the outbox. Fail closed: no fresh <c>ai_enabled</c>, no
/// credentials or no usable provider means no call.
/// </summary>
public sealed class AiGateway
{
    /// <summary>One provider call.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The whole call, every provider together.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(75);

    public const string UsageKind = "ai.usage";
    public const string ProviderEventKind = "ai.provider_event";

    private readonly IRuntimeSettings _settings;
    private readonly IAiCredentialsSource _credentials;
    private readonly IReadOnlyDictionary<string, IAiProviderClient> _clients;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<AiGateway> _logger;

    public AiGateway(
        IRuntimeSettings settings,
        IAiCredentialsSource credentials,
        IEnumerable<IAiProviderClient> clients,
        IServiceScopeFactory scopes,
        TimeProvider time,
        ILogger<AiGateway> logger)
    {
        _settings = settings;
        _credentials = credentials;
        _clients = clients.ToDictionary(c => c.Provider, StringComparer.Ordinal);
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    /// <summary>Overrides of the timeouts (tests).</summary>
    public TimeSpan CallTimeoutValue { get; init; } = CallTimeout;

    public TimeSpan DeadlineValue { get; init; } = Deadline;

    /// <summary>Whether a call could be made now (capabilities): switch on and at least one usable provider.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (!_settings.AiEnabled()) return false;
        var set = await _credentials.GetAsync(cancellationToken);
        return set is { Enabled: true } && Usable(set).Any();
    }

    /// <summary>
    /// Runs one call. <paramref name="parse"/> turns the answer text into the result and returns
    /// null when it does not match the schema (an <c>invalid_output</c>, never a reason to switch).
    /// </summary>
    public async Task<AiCallResult<T>> CallAsync<T>(AiCallRequest request, int pages, Func<string, T?> parse, CancellationToken cancellationToken)
        where T : class
    {
        if (!_settings.AiEnabled()) throw new AiException(AiException.Disabled, 403);
        var set = await _credentials.GetAsync(cancellationToken);
        if (set is not { Enabled: true }) throw new AiException(AiException.Unavailable, 503, "No AI credentials");
        var candidates = Usable(set).Take(Math.Max(1, set.MaxProvidersPerCall)).ToList();
        if (candidates.Count == 0) throw new AiException(AiException.Unavailable, 503, "No usable AI provider");

        // The model may bill and answer whatever reaches us: an expired operation deadline is then uncertain (#72).
        Operations.OperationDeadline.MarkExternalCall();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DeadlineValue);
        var operationId = Guid.NewGuid().ToString("N");

        foreach (var provider in candidates)
        {
            var client = _clients[provider.Provider];
            using var call = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            call.CancelAfter(CallTimeoutValue);
            var started = Stopwatch.GetTimestamp();
            AiProviderAnswer answer;
            try
            {
                answer = await client.SendAsync(provider, request, call.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timed out: the provider may have billed it.
                await RecordAsync(operationId, provider, provider.Model, 0, 0, pages, "uncertain", started);
                // The whole call ran out of time: not this provider's fault, nothing to report.
                if (deadline.IsCancellationRequested) break;
                await SwitchAsync(provider, AiFailover.Classify(new AiFailureInput(provider.Provider, Transport: "timeout")), null);
                continue;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("[ai] {Provider} {ProviderId}: network error ({Error})", provider.Provider, provider.Id, ex.GetType().Name);
                await RecordAsync(operationId, provider, provider.Model, 0, 0, pages, "failed", started);
                await SwitchAsync(provider, AiFailover.Classify(new AiFailureInput(provider.Provider, Transport: "network")), null);
                continue;
            }

            var model = answer.Model ?? provider.Model;
            if (!answer.Success)
            {
                var decision = AiFailover.Classify(new AiFailureInput(provider.Provider, answer.HttpStatus, answer.ErrorType, answer.ErrorMessage));
                await RecordAsync(operationId, provider, model, answer.InputTokens, answer.OutputTokens, pages, "failed", started);
                _logger.LogWarning("[ai] {Provider} {ProviderId} answered {Status} {ErrorType} (rule {Rule}, switch {Switch})",
                    provider.Provider, provider.Id, answer.HttpStatus, answer.ErrorType, decision.Rule, decision.Switch);
                if (decision.Switch)
                {
                    await SwitchAsync(provider, decision, answer.HttpStatus);
                    continue;
                }
                throw new AiException(AiException.ProviderError, 502, $"{provider.Provider} answered {answer.HttpStatus}");
            }

            if (AiFailover.IsRefusal(provider.Provider, answer.StopReason))
            {
                await RecordAsync(operationId, provider, model, answer.InputTokens, answer.OutputTokens, pages, "refused", started);
                throw new AiException(AiException.Refused, 422, $"{provider.Provider} declined ({answer.StopReason})");
            }

            // A cut-off answer (max_tokens / MAX_TOKENS) is as unusable as one off the schema.
            var complete = answer.StopReason is not ("max_tokens" or "MAX_TOKENS");
            var value = complete && !string.IsNullOrWhiteSpace(answer.Text) ? SafeParse(parse, answer.Text!) : null;
            if (value is null)
            {
                await RecordAsync(operationId, provider, model, answer.InputTokens, answer.OutputTokens, pages, "invalid_output", started);
                throw new AiException(AiException.InvalidOutput, 502, $"{provider.Provider} answer does not match the schema ({answer.StopReason})");
            }

            await RecordAsync(operationId, provider, model, answer.InputTokens, answer.OutputTokens, pages, "ok", started);
            return new AiCallResult<T>(value, provider.Provider, model);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new AiException(AiException.Unavailable, 503, "Every AI provider of the call failed");
    }

    /// <summary>
    /// Providers this product can use: a known kind of credential (<c>api_key</c>; an unknown one is
    /// refused, never tried), a known provider, a region the policy allows, not reported into cooldown here.
    /// </summary>
    private IEnumerable<AiProvider> Usable(AiCredentialSet set)
    {
        foreach (var provider in set.Providers)
        {
            string? skip = provider.Credential.Kind != "api_key" ? $"credential kind {provider.Credential.Kind}"
                : !_clients.ContainsKey(provider.Provider) ? $"provider {provider.Provider}"
                : !set.AllowedRegions.Contains(provider.Region, StringComparer.Ordinal) ? $"region {provider.Region}"
                : _credentials.IsCoolingDown(provider.Id) ? "cooldown"
                : null;
            if (skip != null)
            {
                if (skip != "cooldown") _logger.LogWarning("[ai] provider {ProviderId} skipped: unsupported {Reason}", provider.Id, skip);
                continue;
            }
            yield return provider;
        }
    }

    private static T? SafeParse<T>(Func<string, T?> parse, string text) where T : class
    {
        try
        {
            return parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task SwitchAsync(AiProvider provider, AiFailoverDecision decision, int? status)
    {
        _credentials.MarkCoolingDown(provider.Id);
        if (decision.Report is null) return;
        await EnqueueAsync(ProviderEventKind, new AiProviderEvent(provider.Id, decision.Report, status, null), provider.Id);
    }

    private Task RecordAsync(string operationId, AiProvider provider, string model, long input, long output, int pages, string outcome, long started)
    {
        var record = new AiUsageRecord(operationId, provider.Id, model.Length > 120 ? model[..120] : model, input, output, pages, outcome,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, _time.GetUtcNow());
        return EnqueueAsync(UsageKind, new AiUsageBatch([record]), operationId);
    }

    /// <summary>Its own scope and save: the usage stays recorded whatever the request's unit of work does.</summary>
    private async Task EnqueueAsync(string kind, object payload, string subject)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IBackofficeOutbox>();
            outbox.Enqueue(kind, payload, subjectRef: subject);
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ai] could not queue {Kind} for {Subject}", kind, subject);
        }
    }
}

/// <summary>Body of <c>POST /v1/ai/provider-events</c> (snake_case in the outbox).</summary>
public sealed record AiProviderEvent(string ProviderId, string Kind, int? HttpStatus, int? RetryAfterS);

/// <summary>One record of <c>POST /v1/ai/usage</c>.</summary>
public sealed record AiUsageRecord(
    string OperationId,
    string? ProviderId,
    string Model,
    long InputTokens,
    long OutputTokens,
    int? Pages,
    string Outcome,
    long LatencyMs,
    DateTimeOffset OccurredAt);

public sealed record AiUsageBatch(IReadOnlyList<AiUsageRecord> Records);

/// <summary>
/// The AI endpoints may be missing (an installation without the service answers 404): logged, done.
/// </summary>
public abstract class AiOutboxHandler : JsonPostOutboxHandler
{
    public override async Task<OutboxAttemptResult> SendAsync(OutboxRequest request, CancellationToken cancellationToken)
    {
        var result = await base.SendAsync(request, cancellationToken);
        return result.StatusCode == 404 ? OutboxAttemptResult.Response(200) : result;
    }
}

public sealed class AiUsageOutboxHandler : AiOutboxHandler
{
    public override string Kind => AiGateway.UsageKind;

    /// <inheritdoc />
    /// <remarks>The service counts a record once per operation and provider, whatever the key.</remarks>
    public override bool ServiceDeduplicates => true;

    protected override string Path => "v1/ai/usage";
}

public sealed class AiProviderEventOutboxHandler : AiOutboxHandler
{
    public override string Kind => AiGateway.ProviderEventKind;

    protected override string Path => "v1/ai/provider-events";
}
