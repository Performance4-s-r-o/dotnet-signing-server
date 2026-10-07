using DotNetSigningServer.Services.Backoffice.Consents;
using DotNetSigningServer.Services.Consents;

namespace DotNetSigningServer.Tests.Services.Consents;

/// <summary>
/// Which documents a published sentence stands for. Re-consent asks only about what is
/// outstanding, so a sentence naming more than that would describe a decision nobody is
/// making — and one naming less would hide a document the tick does record.
/// </summary>
public class ConsentPromptScopeTests
{
    private static BackofficeConsentPrompt Prompt(params string[] documents) =>
        new("registration", "registration", "registration", true, 1, "h", "cs", "cs", false,
            [new ConsentPromptSegment.Text("Souhlasím s "), .. documents.Select(d => new ConsentPromptSegment.Link(d, d, $"https://legal.test/{d}", 1, "c"))]);

    [Fact]
    public void Documents_are_the_link_segments()
    {
        Assert.Equal(new HashSet<string> { "terms", "dpa" }, Prompt("terms", "dpa").Documents);
        Assert.Empty(Prompt().Documents);
    }

    [Fact]
    public void A_sentence_without_a_document_stands_for_nothing()
    {
        // The service refuses a consent naming a prompt that links to no document.
        Assert.Empty(Prompt().Documents);
    }

    [Fact]
    public void Matching_is_by_set_so_order_does_not_matter()
    {
        Assert.True(Prompt("terms", "dpa").Documents.SetEquals(new HashSet<string> { "dpa", "terms" }));
        Assert.False(Prompt("terms", "dpa").Documents.SetEquals(new HashSet<string> { "terms" }));
        Assert.False(Prompt("terms").Documents.SetEquals(new HashSet<string> { "terms", "dpa" }));
    }
}
