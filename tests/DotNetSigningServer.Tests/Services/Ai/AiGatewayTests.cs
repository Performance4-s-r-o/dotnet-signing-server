using System.Net;
using System.Text.Json;
using DotNetSigningServer.Services.Ai;
using DotNetSigningServer.Tests.Helpers;

namespace DotNetSigningServer.Tests.Services.Ai;

/// <summary>The provider pool: fill-first, failover by the contract, reporting and usage, fail closed.</summary>
public class AiGatewayTests
{
    private static readonly AiCallRequest Request = new("rules", "data", [0x25, 0x50], [], AiSchema.Object(("a", new AiSchema("string", Nullable: true))), 100);

    private static Dictionary<string, JsonElement>? Parse(string text) => AiExtraction.ParseAnswer(text);

    [Fact]
    public async Task The_first_provider_answers_and_its_usage_is_recorded()
    {
        using var host = new AiHost();
        host.Providers.AnthropicAnswers("""{"a":"x"}""");

        var result = await host.Gateway.CallAsync(Request, 3, Parse, CancellationToken.None);

        Assert.Equal("anthropic", result.Provider);
        Assert.Equal("claude-test", result.Model);
        var sent = Assert.Single(host.Providers.Requests);
        Assert.Equal(AiHost.AnthropicKey, Assert.Single(sent.Request.Headers.GetValues("x-api-key")));
        Assert.Equal(AnthropicProviderClient.ApiVersion, Assert.Single(sent.Request.Headers.GetValues("anthropic-version")));
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("json_schema", body.RootElement.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("rules", body.RootElement.GetProperty("system").GetString());
        Assert.Equal("document", body.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("type").GetString());

        var dispatched = await host.DispatchedAsync();
        var usage = Assert.Single(dispatched, d => d.Path == "/v1/ai/usage");
        using var u = JsonDocument.Parse(usage.Body);
        var record = u.RootElement.GetProperty("records")[0];
        Assert.Equal("ok", record.GetProperty("outcome").GetString());
        Assert.Equal(100, record.GetProperty("input_tokens").GetInt32());
        Assert.Equal(3, record.GetProperty("pages").GetInt32());
        Assert.Equal(AiHost.Anthropic().Id, record.GetProperty("provider_id").GetString());
        Assert.DoesNotContain(dispatched, d => d.Body.Contains(AiHost.AnthropicKey) || d.Body.Contains(AiHost.GoogleKey));
        Assert.DoesNotContain(dispatched, d => d.Path == "/v1/ai/provider-events");
    }

    [Theory]
    [InlineData(529, "overloaded_error", "unavailable")]
    [InlineData(429, "rate_limit_error", "rate_limited")]
    [InlineData(402, "billing_error", "credit_exhausted")]
    [InlineData(500, "api_error", "unavailable")]
    public async Task Exhaustion_or_outage_switches_and_is_reported(int status, string type, string report)
    {
        using var host = new AiHost();
        host.Providers.AnthropicError(status, type);
        host.Providers.GoogleAnswers("""{"a":"y"}""");

        var result = await host.Gateway.CallAsync(Request, 1, Parse, CancellationToken.None);

        Assert.Equal("google", result.Provider);
        var google = host.Providers.Requests[1].Request;
        Assert.Equal(AiHost.GoogleKey, Assert.Single(google.Headers.GetValues("x-goog-api-key")));
        Assert.DoesNotContain(AiHost.GoogleKey, google.RequestUri!.ToString());
        Assert.Contains(AiHost.Anthropic().Id, host.Source.Cooling);

        var dispatched = await host.DispatchedAsync();
        var evt = Assert.Single(dispatched, d => d.Path == "/v1/ai/provider-events");
        using var e = JsonDocument.Parse(evt.Body);
        Assert.Equal(report, e.RootElement.GetProperty("kind").GetString());
        Assert.Equal(AiHost.Anthropic().Id, e.RootElement.GetProperty("provider_id").GetString());
        Assert.Equal(2, dispatched.Count(d => d.Path == "/v1/ai/usage"));
    }

    [Fact]
    public async Task The_credit_message_on_a_400_switches()
    {
        using var host = new AiHost();
        host.Providers.AnthropicError(400, "invalid_request_error", "Your credit balance is too low to access the Anthropic API.");
        host.Providers.GoogleAnswers("""{"a":"y"}""");
        Assert.Equal("google", (await host.Gateway.CallAsync(Request, 1, Parse, CancellationToken.None)).Provider);
    }

    [Fact]
    public async Task Another_client_error_does_not_switch()
    {
        using var host = new AiHost();
        host.Providers.AnthropicError(400, "invalid_request_error", "messages: field required");

        var ex = await Assert.ThrowsAsync<AiException>(() => host.Gateway.CallAsync(Request, 1, Parse, CancellationToken.None));

        Assert.Equal(AiException.ProviderError, ex.Code);
        Assert.Single(host.Providers.Requests);
    }

    [Fact]
    public async Task A_refusal_does_not_switch()
    {
        using var host = new AiHost();
        host.Providers.AnthropicAnswers("{}", stop: "refusal");
        var ex = await Assert.ThrowsAsync<AiException>(() => host.Gateway.CallAsync(Request, 1, Parse, CancellationToken.None));
        Assert.Equal(AiException.Refused, ex.Code);
        Assert.Single(host.Providers.Requests);
        var usage = Assert.Single(await host.DispatchedAsync(), d => d.Path == "/v1/ai/usage");
        Assert.Contains("\"refused\"", usage.Body);
    }

    [Fact]
    public async Task Output_off_the_schema_does_not_switch()
    {
        using var host = new AiHost();
        host.Providers.AnthropicAnswers("not json");
        var ex = await Assert.ThrowsAsync<AiException>(() => host.Gateway.CallAsync(Request, 1, Parse, CancellationToken.None));
        Assert.Equal(AiException.InvalidOutput, ex.Code);
        Assert.Single(host.Providers.Requests);
    }

    [Fact]
    public async Task A_cut_off_answer_is_invalid_output()
    {
        using var host = new AiHost();
        host.Providers.AnthropicAnswers("""{"a":"x"}""", stop: "max_tokens");
        var ex = await Assert.ThrowsAsync<AiException>(() => host.Gateway.CallAsync(Request, 1, Parse, CancellationToken.None));
        Assert.Equal(AiException.InvalidOutput, ex.Code);
    }

    [Fact]
    public async Task Gemini_safety_is_a_refusal()
    {
        using var host = new AiHost(AiHost.Credentials(2, AiHost.Google()));
        host.Providers.GoogleAnswers("{}", finish: "SAFETY");
        var ex = await Assert.ThrowsAsync<AiException>(() => host.Gateway.CallAsync(Request, 1, Parse, CancellationToken.None));
        Assert.Equal(AiException.Refused, ex.Code);
    }

    [Fact]
    public async Task A_hanging_provider_times_out_and_the_next_one_answers()
    {
        using var host = new AiHost(callTimeout: TimeSpan.FromMilliseconds(200));
        host.Providers.Anthropic.Enqueue(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return ProviderStub.Json(HttpStatusCode.OK, "{}");
        });
        host.Providers.GoogleAnswers("""{"a":"y"}""");

        var result = await host.Gateway.CallAsync(Request, 1, Parse, CancellationToken.None);

        Assert.Equal("google", result.Provider);
        var dispatched = await host.DispatchedAsync();
        Assert.Contains(dispatched, d => d.Path == "/v1/ai/provider-events" && d.Body.Contains("\"unavailable\""));
        Assert.Contains(dispatched, d => d.Path == "/v1/ai/usage" && d.Body.Contains("\"uncertain\""));
    }

    [Fact]
    public async Task At_most_max_providers_per_call_are_tried()
    {
        using var host = new AiHost(AiHost.Credentials(1));
        host.Providers.AnthropicError(529, "overloaded_error");
        var ex = await Assert.ThrowsAsync<AiException>(() => host.Gateway.CallAsync(Request, 1, Parse, CancellationToken.None));
        Assert.Equal(AiException.Unavailable, ex.Code);
        Assert.Single(host.Providers.Requests);
    }

    [Fact]
    public async Task Unknown_kinds_providers_and_regions_are_skipped_never_used()
    {
        var set = AiHost.Credentials(2,
            AiHost.Anthropic(id: "a1", kind: "access_token"),
            AiHost.Anthropic(id: "a2", region: "us"),
            new AiProvider("a3", "openai", "x", "eu", new AiCredential("api_key", "k")),
            AiHost.Google());
        using var host = new AiHost(set);
        host.Providers.GoogleAnswers("""{"a":"y"}""");

        Assert.Equal("google", (await host.Gateway.CallAsync(Request, 1, Parse, CancellationToken.None)).Provider);
        Assert.Single(host.Providers.Requests);
    }

    [Fact]
    public async Task Without_credentials_or_usable_providers_it_is_unavailable()
    {
        using var empty = new AiHost(new AiCredentialSet(true, DateTimeOffset.UtcNow.AddMinutes(5), ["eu"], 2, []));
        var ex = await Assert.ThrowsAsync<AiException>(() => empty.Gateway.CallAsync(Request, 1, Parse, CancellationToken.None));
        Assert.Equal(AiException.Unavailable, ex.Code);

        using var disabled = new AiHost(new AiCredentialSet(false, DateTimeOffset.UtcNow.AddMinutes(5), ["eu"], 2, [AiHost.Anthropic()]));
        ex = await Assert.ThrowsAsync<AiException>(() => disabled.Gateway.CallAsync(Request, 1, Parse, CancellationToken.None));
        Assert.Equal(AiException.Unavailable, ex.Code);
        Assert.Empty(disabled.Providers.Requests);
    }

    [Fact]
    public async Task With_the_switch_off_nothing_is_called()
    {
        using var host = new AiHost(settings: TestRuntimeSettings.Defaults);
        var ex = await Assert.ThrowsAsync<AiException>(() => host.Gateway.CallAsync(Request, 1, Parse, CancellationToken.None));
        Assert.Equal(AiException.Disabled, ex.Code);
        Assert.Empty(host.Providers.Requests);
        Assert.False(await host.Gateway.IsAvailableAsync(CancellationToken.None));
    }

    [Fact]
    public void A_credential_never_prints_its_value()
    {
        Assert.DoesNotContain(AiHost.AnthropicKey, AiHost.Anthropic().ToString());
    }
}
