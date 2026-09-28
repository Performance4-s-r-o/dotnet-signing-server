using System.Text.Json;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Support;

namespace DotNetSigningServer.Tests.Services.Support;

public class SupportTicketRequestTests
{
    private static readonly User User = new()
    {
        Id = Guid.Parse("7c1f0e2a-4b1d-4a57-9b43-2f7a1f0d0c11"),
        Email = "jana@example.com",
        IsEnterprise = true,
        CreditsRemaining = 42,
    };

    private static JsonElement Json(SupportTicketPayload payload) =>
        JsonDocument.Parse(JsonSerializer.Serialize(payload, BackofficeOutbox.PayloadJson)).RootElement;

    [Fact]
    public void Build_WritesTheServiceShape()
    {
        var payload = SupportTicketRequest.Build(User, new SupportTicketInput(
            "billing", "  Invoice  ", "Hello", "high", "cs", "https://app.example.com/cs/support", "Mozilla/5.0", "1.2.3"));

        var json = Json(payload);
        Assert.Equal(["category", "subject", "message", "reporter", "context", "priority", "source"],
            json.EnumerateObject().Select(p => p.Name));
        Assert.Equal("billing", json.GetProperty("category").GetString());
        Assert.Equal("Invoice", json.GetProperty("subject").GetString());
        Assert.Equal("high", json.GetProperty("priority").GetString());
        Assert.Equal("in_app", json.GetProperty("source").GetString());
        var reporter = json.GetProperty("reporter");
        Assert.Equal("jana@example.com", reporter.GetProperty("email").GetString());
        Assert.Equal("jana@example.com", reporter.GetProperty("name").GetString());
        Assert.Equal("dotnet:user:7c1f0e2a-4b1d-4a57-9b43-2f7a1f0d0c11", reporter.GetProperty("subject_ref").GetString());
    }

    [Fact]
    public void Context_HasNoFurtherPersonalData()
    {
        var json = Json(SupportTicketRequest.Build(User, new SupportTicketInput(
            "other", "s", "m", "normal", "en", "https://app.example.com/support", "UA", "1.0")));

        var context = json.GetProperty("context");
        Assert.Equal(["plan", "locale", "url", "app_version", "user_agent"], context.EnumerateObject().Select(p => p.Name));
        Assert.Equal("enterprise", context.GetProperty("plan").GetString());
        Assert.DoesNotContain("42", context.GetRawText());
        Assert.DoesNotContain("jana@example.com", context.GetRawText());
    }

    [Fact]
    public void Context_LeavesOutWhatIsMissingOrInvalid()
    {
        var standard = new User { Id = Guid.NewGuid(), Email = "a@example.com" };
        var json = Json(SupportTicketRequest.Build(standard, new SupportTicketInput(
            "other", "s", "m", null, Locale: "czech", Url: "javascript:alert(1)", UserAgent: " ", AppVersion: null)));

        var context = json.GetProperty("context");
        Assert.Equal(["plan"], context.EnumerateObject().Select(p => p.Name));
        Assert.Equal("standard", context.GetProperty("plan").GetString());
        Assert.Equal("normal", json.GetProperty("priority").GetString());
    }

    [Fact]
    public void Limits_AreApplied()
    {
        var payload = SupportTicketRequest.Build(User, new SupportTicketInput(
            "other", new string('s', 500), new string('m', 9000), "normal", UserAgent: new string('u', 900),
            AppVersion: new string('v', 80)));

        Assert.Equal(SupportTicketRequest.MaxSubjectLength, payload.Subject.Length);
        Assert.Equal(SupportTicketRequest.MaxMessageLength, payload.Message.Length);
        Assert.Equal(SupportTicketRequest.MaxUserAgentLength, payload.Context.UserAgent!.Length);
        Assert.Equal(SupportTicketRequest.MaxAppVersionLength, payload.Context.AppVersion!.Length);
    }

    [Fact]
    public void Truncate_DoesNotSplitASurrogatePair()
    {
        var text = new string('a', SupportTicketRequest.MaxSubjectLength - 1) + "😀";

        var subject = SupportTicketRequest.NormalizeSubject(text);

        Assert.Equal(SupportTicketRequest.MaxSubjectLength - 1, subject.Length);
        Assert.False(char.IsHighSurrogate(subject[^1]));
    }

    [Fact]
    public void Message_IsPlainTextAsTyped()
    {
        const string message = "<script>alert('x')</script>\r\n<b>bold</b> & \"quotes\"";

        var payload = SupportTicketRequest.Build(User, new SupportTicketInput("other", "s", message, "normal"));

        Assert.Equal("<script>alert('x')</script>\n<b>bold</b> & \"quotes\"", payload.Message);
        Assert.DoesNotContain("&lt;", payload.Message);
        Assert.DoesNotContain("data:text/html", payload.Message);
    }

    [Fact]
    public void Subject_IsOneLine()
    {
        Assert.Equal("a b c", SupportTicketRequest.NormalizeSubject("a\r\nb\t c "));
    }

    [Theory]
    [InlineData("billing", "billing")]
    [InlineData("unknown", "other")]
    [InlineData("Billing", "other")]
    [InlineData(null, "other")]
    public void Category_MustBeOffered(string? category, string expected)
    {
        Assert.Equal(expected, SupportTicketRequest.NormalizeCategory(category, SupportTicketRequest.DefaultCategories));
    }

    [Fact]
    public void Category_WithoutOther_FallsBackToTheFirstOffered()
    {
        Assert.Equal("booking", SupportTicketRequest.NormalizeCategory("nope", ["booking", "payments"]));
    }

    [Fact]
    public void Url_DropsQueryAndFragment()
    {
        Assert.Equal("https://app.example.com/cs/support",
            SupportTicketRequest.NormalizeUrl("https://app.example.com/cs/support?token=secret#x"));
    }

    [Theory]
    [InlineData("cs", "cs")]
    [InlineData("en-US", "en-US")]
    [InlineData("EN", null)]
    [InlineData("", null)]
    public void Locale_MatchesTheServicePattern(string locale, string? expected)
    {
        Assert.Equal(expected, SupportTicketRequest.NormalizeLocale(locale));
    }

    [Fact]
    public void ReporterName_IsNeverEmpty()
    {
        Assert.Equal("User", SupportTicketRequest.ReporterName(""));
        Assert.Equal("a@example.com", SupportTicketRequest.ReporterName(" a@example.com "));
    }

    [Theory]
    [InlineData("0e6a…", "482193", "0e6a…#482193", "482193")]
    [InlineData("0e6a", null, "0e6a", null)]
    [InlineData(null, "482193", "#482193", "482193")]
    public void Receipt_KeepsTheTicketNumber(string? id, string? number, string expected, string? parsed)
    {
        var remoteId = SupportTicketReceipt.Format(id, number);

        Assert.Equal(expected, remoteId);
        Assert.Equal(parsed, SupportTicketReceipt.TicketNumber(remoteId));
    }

    [Fact]
    public void Receipt_TooLong_KeepsTheNumber()
    {
        var remoteId = SupportTicketReceipt.Format(new string('i', 60), "482193");

        Assert.Equal("#482193", remoteId);
    }
}
