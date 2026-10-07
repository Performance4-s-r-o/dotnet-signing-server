using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Consents;

namespace DotNetSigningServer.Tests.Services.Consents;

/// <summary>
/// The service refuses a consent whose prompt is not linked to that document
/// (`consent_prompt_document_mismatch`), and a refused batch sits in the outbox retrying.
/// So the reference rides along only with the documents the sentence mentioned, and never
/// with an acknowledgement — that is a notice, not a decision made against a sentence.
/// </summary>
public class ConsentPromptRefTests
{
    private static readonly ConsentPromptRef Prompt =
        new("registration", 3, "h", new HashSet<string>(StringComparer.Ordinal) { "terms", "dpa" });

    private static ConsentRecord Record(string document, string action) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        SubjectRef = "dotnet:user:1",
        Document = document,
        Purpose = document,
        Version = 1,
        Locale = "cs",
        Action = action,
        Source = "signup",
        Channel = "web",
        OccurredAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Rides_along_with_the_documents_the_sentence_mentioned()
    {
        var batch = ConsentService.Batch(
            [Record("terms", ConsentActions.Granted), Record("dpa", ConsentActions.Granted)],
            ip: null,
            ConsentService.Flows.Signup,
            prompt: Prompt);

        Assert.All(batch.Events, e => Assert.Same(Prompt, e.Prompt));
    }

    [Fact]
    public void Is_left_off_a_document_the_sentence_never_mentioned()
    {
        var batch = ConsentService.Batch(
            [Record("terms", ConsentActions.Granted), Record("sla", ConsentActions.Granted)],
            ip: null,
            ConsentService.Flows.Signup,
            prompt: Prompt);

        Assert.NotNull(Assert.Single(batch.Events, e => e.Document == "terms").Prompt);
        Assert.Null(Assert.Single(batch.Events, e => e.Document == "sla").Prompt);
    }

    [Fact]
    public void Is_left_off_an_acknowledgement()
    {
        var batch = ConsentService.Batch(
            [Record("terms", ConsentActions.Acknowledged)],
            ip: null,
            ConsentService.Flows.Signup,
            prompt: Prompt);

        Assert.Null(Assert.Single(batch.Events).Prompt);
    }

    [Fact]
    public void Is_absent_when_the_form_showed_this_app_s_own_wording()
    {
        var batch = ConsentService.Batch(
            [Record("terms", ConsentActions.Granted)],
            ip: null,
            ConsentService.Flows.Signup);

        Assert.Null(Assert.Single(batch.Events).Prompt);
    }
}
