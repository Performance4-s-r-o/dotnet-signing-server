using System.Net;
using DotNetSigningServer.Services.Backoffice.Consents;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services.Backoffice.Consents;

/// <summary>
/// A sign-up page shows the copy it has and never waits for the service, so reading and
/// revalidating are separate calls. What matters: a fresh entry is served without asking,
/// a 304 keeps the wording while marking it fresh again, and a service that fails leaves
/// the copy in place rather than the page without a sentence.
/// </summary>
public class ConsentPromptsCacheTests : IDisposable
{
    private const string V1 = """
        {"data":[{"key":"terms","purpose":"terms","context":"registration","required":true,
          "prompt_version":1,"prompt_hash":"h1","locale":"cs","requested_locale":"cs","fallback":false,
          "segments":[{"type":"text","value":"Souhlasím."}]}]}
        """;

    private const string V2 = """
        {"data":[{"key":"terms","purpose":"terms","context":"registration","required":true,
          "prompt_version":2,"prompt_hash":"h2","locale":"cs","requested_locale":"cs","fallback":false,
          "segments":[{"type":"text","value":"Souhlasím s novým zněním."}]}]}
        """;

    private readonly StubServiceHandler _service = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());
    private readonly ServiceProvider _services;

    public ConsentPromptsCacheTests()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(BackofficeConsentPromptsClient.HttpClientName, c => c.BaseAddress = new Uri("https://backoffice.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => _service);
        _services = services.BuildServiceProvider();
    }

    private ConsentPromptsCache Cache(TimeSpan? ttl = null) =>
        new(new BackofficeConsentPromptsClient(_services.GetRequiredService<IHttpClientFactory>()), _memory, _time, ttl);

    /// <summary>The service always serves an ETag; without one there is nothing to revalidate with.</summary>
    private void EnqueueTagged(string json, string etag) =>
        _service.Responses.Enqueue(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{etag}\"") },
        }));

    [Fact]
    public async Task Peek_answers_from_memory_and_says_when_it_is_stale()
    {
        var cache = Cache(TimeSpan.FromMinutes(5));
        EnqueueTagged(V1, "v1");
        await cache.RefreshAsync("registration", "cs", CancellationToken.None);

        var entry = cache.Peek("registration", "cs")!;
        Assert.True(cache.IsFresh(entry));
        Assert.Equal("Souhlasím.", Assert.IsType<ConsentPromptSegment.Text>(Assert.Single(entry.Prompts).Segments[0]).Value);

        _time.Advance(TimeSpan.FromMinutes(6));
        Assert.False(cache.IsFresh(cache.Peek("registration", "cs")!));
        // Still only the one call: going stale is not a reason to ask on its own.
        Assert.Single(_service.Requests);
    }

    [Fact]
    public async Task A_304_keeps_the_wording_and_makes_it_fresh_again()
    {
        var cache = Cache(TimeSpan.FromMinutes(5));
        EnqueueTagged(V1, "v1");
        await cache.RefreshAsync("registration", "cs", CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(6));

        _service.Enqueue(HttpStatusCode.NotModified);
        var prompts = await cache.RefreshAsync("registration", "cs", CancellationToken.None);

        Assert.Equal(1, Assert.Single(prompts).PromptVersion);
        Assert.True(cache.IsFresh(cache.Peek("registration", "cs")!));
    }

    [Fact]
    public async Task A_new_published_version_replaces_the_copy()
    {
        var cache = Cache();
        EnqueueTagged(V1, "v1");
        await cache.RefreshAsync("registration", "cs", CancellationToken.None);

        EnqueueTagged(V2, "v2");
        var prompts = await cache.RefreshAsync("registration", "cs", CancellationToken.None);

        Assert.Equal(2, Assert.Single(prompts).PromptVersion);
        Assert.Equal("h2", prompts[0].PromptHash);
    }

    [Fact]
    public async Task A_service_that_fails_leaves_the_copy_in_place()
    {
        var cache = Cache();
        EnqueueTagged(V1, "v1");
        await cache.RefreshAsync("registration", "cs", CancellationToken.None);

        _service.Enqueue(HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<HttpRequestException>(
            () => cache.RefreshAsync("registration", "cs", CancellationToken.None));

        Assert.Equal(1, Assert.Single(cache.Peek("registration", "cs")!.Prompts).PromptVersion);
    }

    [Fact]
    public async Task Contexts_and_locales_are_kept_apart()
    {
        var cache = Cache();
        EnqueueTagged(V1, "v1");
        await cache.RefreshAsync("registration", "cs", CancellationToken.None);

        Assert.Null(cache.Peek("registration", "en"));
        Assert.Null(cache.Peek("checkout", "cs"));

        cache.Remove("registration", "cs");
        Assert.Null(cache.Peek("registration", "cs"));
    }

    public void Dispose()
    {
        _memory.Dispose();
        _services.Dispose();
        _service.Dispose();
        GC.SuppressFinalize(this);
    }
}
