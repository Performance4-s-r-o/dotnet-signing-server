using DotNetSigningServer.Services.Backoffice.Consents;
using DotNetSigningServer.Services.Consents;

namespace DotNetSigningServer.Tests.Services.Consents;

public class ConsentPromptSelectionTests
{
    private static BackofficeConsentPrompt Prompt(string key, bool required) =>
        new(key, key, "registration", required, 1, "h", "cs", "cs", false, [new ConsentPromptSegment.Text(key)]);

    [Fact]
    public void Shows_the_published_sentence_when_there_is_exactly_one_required_consent()
    {
        var picked = ConsentPromptSelection.ForSingleCheckbox([Prompt("registration", true), Prompt("newsletter", false)]);

        Assert.Equal("registration", picked!.Key);
    }

    // Two required consents are two decisions; one checkbox cannot record them.
    [Fact]
    public void Falls_back_when_the_service_asks_for_more_than_one_consent()
    {
        Assert.Null(ConsentPromptSelection.ForSingleCheckbox([Prompt("terms", true), Prompt("dpa", true)]));
    }

    [Fact]
    public void Falls_back_when_there_is_nothing_to_show()
    {
        Assert.Null(ConsentPromptSelection.ForSingleCheckbox(null));
        Assert.Null(ConsentPromptSelection.ForSingleCheckbox([]));
        Assert.Null(ConsentPromptSelection.ForSingleCheckbox([Prompt("newsletter", false)]));
    }
}
