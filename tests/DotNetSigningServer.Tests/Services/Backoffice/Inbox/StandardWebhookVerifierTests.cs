using DotNetSigningServer.Services.Backoffice.Inbox;

namespace DotNetSigningServer.Tests.Services.Backoffice.Inbox;

public class StandardWebhookVerifierTests
{
    private const string Secret = InboxTestHost.Secret;
    private const string Body = """{"type":"price.scheduled","timestamp":"2026-09-28T12:00:00Z","data":{"version":2}}""";
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static VerifiedWebhook Verify(string? signature, long? timestamp = null, string body = Body, string? previous = null) =>
        new StandardWebhookVerifier(new[] { Secret, previous })
            .Verify(body, "msg_1", (timestamp ?? Now.ToUnixTimeSeconds()).ToString(), signature, Now);

    [Fact]
    public void ValidSignature_ReturnsTheMessage()
    {
        var ts = Now.ToUnixTimeSeconds();

        var message = Verify(StandardWebhookVerifier.Sign(Secret, "msg_1", ts, Body), ts);

        Assert.Equal("msg_1", message.Id);
        Assert.Equal("price.scheduled", message.Type);
        Assert.Equal(2, message.Data.GetProperty("version").GetInt32());
    }

    [Fact]
    public void WrongSecret_IsRejected()
    {
        var ts = Now.ToUnixTimeSeconds();
        var signature = StandardWebhookVerifier.Sign(InboxTestHost.PreviousSecret, "msg_1", ts, Body);

        Assert.Throws<BackofficeWebhookVerificationException>(() => Verify(signature, ts));
    }

    [Fact]
    public void Rotation_AcceptsThePreviousSecret()
    {
        var ts = Now.ToUnixTimeSeconds();
        var signature = StandardWebhookVerifier.Sign(InboxTestHost.PreviousSecret, "msg_1", ts, Body);

        Assert.Equal("msg_1", Verify(signature, ts, previous: InboxTestHost.PreviousSecret).Id);
    }

    [Fact]
    public void Rotation_AcceptsEitherOfTwoSignatures()
    {
        // During a rotation the service sends one signature per secret.
        var ts = Now.ToUnixTimeSeconds();
        var signature = StandardWebhookVerifier.Sign(InboxTestHost.PreviousSecret, "msg_1", ts, Body)
                        + " " + StandardWebhookVerifier.Sign(Secret, "msg_1", ts, Body);

        Assert.Equal("msg_1", Verify(signature, ts).Id);
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void TimestampOutsideFiveMinutes_IsRejected(int offsetSeconds)
    {
        var ts = Now.ToUnixTimeSeconds() + offsetSeconds;

        Assert.Throws<BackofficeWebhookVerificationException>(
            () => Verify(StandardWebhookVerifier.Sign(Secret, "msg_1", ts, Body), ts));
    }

    [Fact]
    public void TamperedBody_IsRejected()
    {
        var ts = Now.ToUnixTimeSeconds();
        var signature = StandardWebhookVerifier.Sign(Secret, "msg_1", ts, Body);

        Assert.Throws<BackofficeWebhookVerificationException>(
            () => Verify(signature, ts, body: Body.Replace("2}", "3}")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1,not-base64!")]
    [InlineData("v2,AAAA")]
    public void MissingOrMalformedSignature_IsRejected(string? signature)
    {
        Assert.Throws<BackofficeWebhookVerificationException>(() => Verify(signature));
    }

    [Fact]
    public void SignedPayloadWithoutType_IsRejected()
    {
        var ts = Now.ToUnixTimeSeconds();
        const string body = """{"data":{}}""";

        Assert.Throws<BackofficeWebhookVerificationException>(
            () => Verify(StandardWebhookVerifier.Sign(Secret, "msg_1", ts, body), ts, body));
    }

    [Fact]
    public void NoSecret_IsAWiringError()
    {
        Assert.Throws<ArgumentException>(() => new StandardWebhookVerifier(new string?[] { null, " " }));
    }

}
