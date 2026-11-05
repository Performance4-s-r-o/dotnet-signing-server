using System.Net;
using DotNetSigningServer.Services.Ai;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Ai;

/// <summary>POST /v1/ai/credentials: cached until expires_at, never extended, kept on a failed refresh.</summary>
public class AiCredentialsClientTests
{
    private static string Body(DateTimeOffset expires, bool enabled = true) =>
        $$$"""
        {"enabled":{{{(enabled ? "true" : "false")}}},"expires_at":"{{{expires:O}}}",
         "policy":{"allowed_regions":["eu"],"max_providers_per_call":2},
         "providers":[{"id":"p1","provider":"anthropic","model":"m","region":"eu","credential":{"kind":"api_key","value":"secret"}}]}
        """;

    private static (AiCredentialsClient Client, StubServiceHandler Service, ManualTimeProvider Time, ServiceProvider Provider) Create()
    {
        var service = new StubServiceHandler();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddHttpClient(AiCredentialsClient.HttpClientName, c => c.BaseAddress = new Uri(OutboxTestHost.BaseUrl))
            .ConfigurePrimaryHttpMessageHandler(() => service);
        var provider = services.BuildServiceProvider();
        return (new AiCredentialsClient(provider.GetRequiredService<IHttpClientFactory>(), time, NullLogger<AiCredentialsClient>.Instance), service, time, provider);
    }

    [Fact]
    public async Task Cached_until_expiry_then_refused_when_the_refresh_fails()
    {
        var (client, service, time, provider) = Create();
        using var _ = provider;
        service.Enqueue(HttpStatusCode.OK, Body(time.Now.AddSeconds(600)));

        var set = await client.GetAsync(CancellationToken.None);
        Assert.NotNull(set);
        Assert.Equal("secret", set!.Providers[0].Credential.Value);
        Assert.Equal("POST", service.Requests[0].Request.Method.Method);
        Assert.EndsWith("v1/ai/credentials", service.Requests[0].Request.RequestUri!.ToString());

        time.Advance(TimeSpan.FromSeconds(100));
        Assert.Same(set, await client.GetAsync(CancellationToken.None));
        Assert.Single(service.Requests);

        // Past expiry: a failed refresh leaves nothing.
        service.Enqueue(HttpStatusCode.ServiceUnavailable);
        time.Advance(TimeSpan.FromSeconds(600));
        Assert.Null(await client.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Enabled_false_replaces_the_copy_at_once()
    {
        var (client, service, time, provider) = Create();
        using var _ = provider;
        service.Enqueue(HttpStatusCode.OK, Body(time.Now.AddSeconds(60)));
        service.Enqueue(HttpStatusCode.OK, Body(time.Now.AddSeconds(660), enabled: false));
        Assert.True((await client.GetAsync(CancellationToken.None))!.Enabled);
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.False((await client.GetAsync(CancellationToken.None))!.Enabled);
    }

    [Fact]
    public void Parse_reads_the_contract_shape()
    {
        var set = AiCredentialSet.Parse(Body(DateTimeOffset.UtcNow));
        Assert.Equal(["eu"], set.AllowedRegions);
        Assert.Equal(2, set.MaxProvidersPerCall);
        Assert.Equal("api_key", set.Providers[0].Credential.Kind);
    }
}
