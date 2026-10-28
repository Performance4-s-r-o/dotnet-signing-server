using DotNetSigningServer.Controllers;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Services;

/// <summary>#72: samostatný limit volání TSA, deadline inspect-signatures a odpojení klienta.</summary>
[Collection("TsaStatics")]
public class DeadlinesAndGatesTests
{
    private sealed class ExposedTsa : PinnedIpTsaClient
    {
        public ExposedTsa() : base("https://tsa.invalid.example/tsr", null, null) { }
        public byte[] Call() => GetTSAResponse(new byte[] { 1 });
    }

    private sealed class Throwing : iText.Signatures.ITSAClient
    {
        public int Calls;
        public int GetTokenSizeEstimate() => 1;
        public iText.Commons.Digest.IMessageDigest GetMessageDigest() => throw new NotSupportedException();
        public byte[] GetTimeStampToken(byte[] imprint) { Calls++; throw new TsaCommunicationException("u", "x", new TsaBusyException()); }
    }

    [Fact]
    public void A_full_gate_refuses_as_busy_without_touching_the_gate_count()
    {
        var saved = (PinnedIpTsaClient.CallGate, PinnedIpTsaClient.GateWait);
        try
        {
            PinnedIpTsaClient.CallGate = new SemaphoreSlim(0, 1);
            PinnedIpTsaClient.GateWait = TimeSpan.FromMilliseconds(30);
            var ex = Assert.Throws<TsaCommunicationException>(() => new ExposedTsa().Call());
            Assert.IsType<TsaBusyException>(ex.InnerException);
            Assert.Equal(0, PinnedIpTsaClient.CallGate.CurrentCount);
        }
        finally { (PinnedIpTsaClient.CallGate, PinnedIpTsaClient.GateWait) = saved; }
    }

    [Fact]
    public void A_failed_call_gives_its_place_back()
    {
        var saved = PinnedIpTsaClient.CallGate;
        try
        {
            PinnedIpTsaClient.CallGate = new SemaphoreSlim(1, 1);
            Assert.ThrowsAny<Exception>(() => new ExposedTsa().Call());
            Assert.Equal(1, PinnedIpTsaClient.CallGate.CurrentCount);
        }
        finally { PinnedIpTsaClient.CallGate = saved; }
    }

    [Fact]
    public void A_busy_primary_does_not_send_the_call_to_the_backup()
    {
        var primary = new Throwing();
        var backup = new Throwing();
        var client = new FallbackTsaClient(primary, backup, "https://a", "https://b");
        Assert.Throws<TsaCommunicationException>(() => client.GetTimeStampToken(new byte[] { 1 }));
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, backup.Calls);
    }

    [Fact]
    public void The_call_over_the_limit_is_refused_and_passes_once_a_place_is_free()
    {
        var saved = (PinnedIpTsaClient.CallGate, PinnedIpTsaClient.GateWait);
        try
        {
            PinnedIpTsaClient.SetMaxConcurrentCalls(2);
            PinnedIpTsaClient.GateWait = TimeSpan.FromMilliseconds(30);
            var gate = PinnedIpTsaClient.CallGate;
            gate.Wait(); gate.Wait(); // two calls in flight
            var ex = Assert.Throws<TsaCommunicationException>(() => new ExposedTsa().Call());
            Assert.IsType<TsaBusyException>(ex.InnerException);
            gate.Release();
            // A place is free: the call goes on and fails for another reason (the host is not real).
            var other = Assert.ThrowsAny<Exception>(() => new ExposedTsa().Call());
            Assert.False(other.InnerException is TsaBusyException);
            gate.Release();
            Assert.Equal(2, gate.CurrentCount);
        }
        finally { (PinnedIpTsaClient.CallGate, PinnedIpTsaClient.GateWait) = saved; }
    }

    [Fact]
    public void A_busy_chain_is_not_an_uncertain_failure()
    {
        var busy = new TsaCommunicationException("u", "x", new TsaBusyException());
        Assert.False(DotNetSigningServer.Controllers.ApiControllerBase.IsUncertainFailure(busy));
        Assert.True(DotNetSigningServer.Controllers.ApiControllerBase.IsUncertainFailure(new TsaCommunicationException("u", "x")));
    }

    [Fact]
    public void The_limit_setting_refuses_nonsense()
    {
        Assert.Throws<InvalidOperationException>(() => PinnedIpTsaClient.SetMaxConcurrentCalls(0));
    }

    // ── inspect-signatures ───────────────────────────────────────────────

    private sealed class Inspector : PdfSigningApiController
    {
        public Inspector(HttpContext context, IStringLocalizer<SharedStrings> localizer)
            : base(null!, null!, null!, new Mock<IApiAuthService>().Object, NullLogger<PdfSigningApiController>.Instance,
                new ContentLimitGuard(TestHelpers.WrapOptions(new LimitsOptions())), TestHelpers.WrapOptions(new BillingOptions()),
                TestHelpers.CreateMockEnvironment(), null!,
                new Mock<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>().Object, localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = context };
        }

        public IActionResult BusyResult(TsaCommunicationException ex) => TsaUnreachableResult(ex);
    }

    [Fact]
    public void Busy_maps_to_503_TSA_BUSY_with_retry_after()
    {
        var ctx = new DefaultHttpContext();
        var controller = new Inspector(ctx, Localizer());
        var result = controller.BusyResult(new TsaCommunicationException("u", "x", new TsaBusyException()));
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, obj.StatusCode);
        Assert.Contains("TSA_BUSY", obj.Value!.ToString());
        Assert.Equal("1", ctx.Response.Headers.RetryAfter.ToString());
    }

    private static byte[] SmallPdf()
    {
        using var ms = new MemoryStream();
        using (var doc = new Document(new PdfDocument(new PdfWriter(ms)))) doc.Add(new Paragraph("x"));
        return ms.ToArray();
    }

    private static IStringLocalizer<SharedStrings> Localizer()
    {
        var l = new Mock<IStringLocalizer<SharedStrings>>();
        l.Setup(x => x[It.IsAny<string>()]).Returns((string k) => new LocalizedString(k, k));
        return l.Object;
    }

    [Fact]
    public async Task Inspection_over_its_deadline_answers_504_with_a_stable_code()
    {
        var saved = PdfSigningApiController.InspectDeadline;
        try
        {
            PdfSigningApiController.InspectDeadline = TimeSpan.Zero; // already expired: a 1 ms limit races a tiny PDF
            var result = await new Inspector(new DefaultHttpContext(), Localizer()).InspectSignaturesCore(SmallPdf());
            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(504, obj.StatusCode);
            Assert.Contains("INSPECT_DEADLINE_EXCEEDED", obj.Value!.ToString());
        }
        finally { PdfSigningApiController.InspectDeadline = saved; }
    }

    [Fact]
    public async Task A_client_that_went_away_gets_no_answer_and_no_error()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cts.Token };
        var result = await new Inspector(context, Localizer()).InspectSignaturesCore(SmallPdf());
        Assert.IsType<EmptyResult>(result);
    }

    [Fact]
    public async Task A_normal_inspection_still_answers_200()
    {
        var result = await new Inspector(new DefaultHttpContext(), Localizer()).InspectSignaturesCore(SmallPdf());
        Assert.IsType<OkObjectResult>(result);
    }
}

[CollectionDefinition("TsaStatics", DisableParallelization = true)]
public class TsaStaticsCollection { }
