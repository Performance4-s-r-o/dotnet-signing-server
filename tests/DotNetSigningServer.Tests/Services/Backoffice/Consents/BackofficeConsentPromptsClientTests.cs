using System.Net;
using DotNetSigningServer.Services.Backoffice.Consents;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services.Backoffice.Consents;

/// <summary>
/// The wording of a consent comes from the backoffice service as segments.
/// What matters here: the version and hash travel with it (they go back with the consent),
/// a segment this app does not understand does not take the rest of the sentence with it,
/// and a revalidation that answers 304 keeps the copy we have.
/// </summary>
public class BackofficeConsentPromptsClientTests : IDisposable
{
    private const string Body = """
        {"data":[
          {"key":"terms","purpose":"terms","context":"registration","required":true,
           "prompt_version":3,"prompt_hash":"abc123","locale":"cs","requested_locale":"cs","fallback":false,
           "segments":[
             {"type":"text","value":"Souhlasím s "},
             {"type":"link","document_type":"terms","title":"obchodními podmínkami","url":"https://legal.test/p4-dotnet/terms","version":2,"content_hash":"deadbeef"},
             {"type":"text","value":"."}
           ]},
          {"key":"newsletter","purpose":"newsletter","context":"registration","required":false,
           "prompt_version":1,"prompt_hash":"def456","locale":"en","requested_locale":"cs","fallback":true,
           "segments":[{"type":"text","value":"Send me news."},{"type":"blink","value":"?"}]},
          {"key":"broken","purpose":"x","context":"registration","required":false,
           "prompt_version":0,"prompt_hash":"","locale":"cs","requested_locale":"cs","fallback":false,
           "segments":[{"type":"text","value":"x"}]},
          {"key":"empty","purpose":"x","context":"registration","required":false,
           "prompt_version":1,"prompt_hash":"h","locale":"cs","requested_locale":"cs","fallback":false,
           "segments":[]}
        ]}
        """;

    private readonly StubServiceHandler _service = new();
    private readonly ServiceProvider _services;

    public BackofficeConsentPromptsClientTests()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(BackofficeConsentPromptsClient.HttpClientName, c => c.BaseAddress = new Uri("https://backoffice.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => _service);
        _services = services.BuildServiceProvider();
    }

    private BackofficeConsentPromptsClient Client() =>
        new(_services.GetRequiredService<IHttpClientFactory>());

    [Fact]
    public async Task Reads_segments_with_the_version_and_hash_that_go_back_with_the_consent()
    {
        _service.Enqueue(HttpStatusCode.OK, Body);

        var fetch = await Client().GetPromptsAsync("cs", "registration", null, CancellationToken.None);

        Assert.False(fetch.NotModified);
        var terms = Assert.Single(fetch.Prompts!, p => p.Key == "terms");
        Assert.Equal(3, terms.PromptVersion);
        Assert.Equal("abc123", terms.PromptHash);
        Assert.True(terms.Required);
        Assert.Collection(
            terms.Segments,
            s => Assert.Equal("Souhlasím s ", Assert.IsType<ConsentPromptSegment.Text>(s).Value),
            s =>
            {
                var link = Assert.IsType<ConsentPromptSegment.Link>(s);
                Assert.Equal("terms", link.DocumentType);
                Assert.Equal("obchodními podmínkami", link.Title);
                Assert.Equal("https://legal.test/p4-dotnet/terms", link.Url);
                Assert.Equal(2, link.Version);
            },
            s => Assert.Equal(".", Assert.IsType<ConsentPromptSegment.Text>(s).Value));
    }

    [Fact]
    public async Task Keeps_a_sentence_whose_other_segment_is_of_a_kind_this_app_does_not_know()
    {
        _service.Enqueue(HttpStatusCode.OK, Body);

        var fetch = await Client().GetPromptsAsync("cs", "registration", null, CancellationToken.None);

        var newsletter = Assert.Single(fetch.Prompts!, p => p.Key == "newsletter");
        Assert.Equal("Send me news.", Assert.IsType<ConsentPromptSegment.Text>(Assert.Single(newsletter.Segments)).Value);
        // Served in another language than asked for; the page says so.
        Assert.True(newsletter.Fallback);
        Assert.Equal("en", newsletter.Locale);
    }

    [Fact]
    public async Task Drops_a_prompt_that_could_not_be_recorded_or_shown()
    {
        _service.Enqueue(HttpStatusCode.OK, Body);

        var fetch = await Client().GetPromptsAsync("cs", "registration", null, CancellationToken.None);

        // No version or hash: a consent against it could not say what was agreed to.
        Assert.DoesNotContain(fetch.Prompts!, p => p.Key == "broken");
        // No segments: there would be nothing next to the checkbox.
        Assert.DoesNotContain(fetch.Prompts!, p => p.Key == "empty");
    }

    [Fact]
    public async Task Revalidates_with_the_etag_and_keeps_what_we_have_on_304()
    {
        _service.Enqueue(HttpStatusCode.NotModified);

        var fetch = await Client().GetPromptsAsync("cs", "registration", "\"v1\"", CancellationToken.None);

        Assert.True(fetch.NotModified);
        Assert.Equal("\"v1\"", fetch.ETag);
        var sent = Assert.Single(_service.Requests).Request;
        Assert.Equal("\"v1\"", Assert.Single(sent.Headers.IfNoneMatch).ToString());
        Assert.Equal("/v1/consent-prompts?locale=cs&context=registration", sent.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Throws_when_the_service_fails_so_the_caller_serves_its_copy()
    {
        _service.Enqueue(HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Client().GetPromptsAsync("cs", "registration", null, CancellationToken.None));
    }

    public void Dispose()
    {
        _services.Dispose();
        _service.Dispose();
        GC.SuppressFinalize(this);
    }
}
