using System.Diagnostics;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Operations;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// #72: a slow or silent TSA, OCSP responder or CRL server, and a client that goes away, against
/// the deadline. No network: the HTTP client gets a handler that answers as slowly as the test says.
/// </summary>
[Collection("TsaStatics")]
public class SlowPeersTests
{
    /// <summary>Hangs until cancelled, or sends the headers at once and then drips the body.</summary>
    private sealed class SlowHandler(bool headersAtOnce) : HttpMessageHandler
    {
        public int Requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            if (!headersAtOnce)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new DrippingStream()) };
        }

        // HttpClient.Send is synchronous and a custom handler has to opt in.
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct) =>
            SendAsync(request, ct).GetAwaiter().GetResult();
    }

    /// <summary>One byte, then silence until cancelled: a responder that never finishes.</summary>
    private sealed class DrippingStream : Stream
    {
        private bool _first = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_first)
            {
                _first = false;
                buffer.Span[0] = 1;
                return 1;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private static GuardedResourceRetriever Retriever(SlowHandler handler, TimeSpan? bodyTimeout = null) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, _ => true, bodyTimeout);

    /// <summary>Runs <paramref name="call"/> as an operation with this deadline and times it.</summary>
    private static async Task<(Exception? Error, TimeSpan Elapsed)> UnderDeadline(
        TimeSpan limit, Action call, CancellationToken caller = default)
    {
        using var deadline = new OperationDeadline(limit, caller);
        var sw = Stopwatch.StartNew();
        Exception? error = null;
        try
        {
            await deadline.Run(_ => { call(); return Task.FromResult(0); }).WaitAsync(Bound);
        }
        catch (TimeoutException) { throw new Xunit.Sdk.XunitException("The call outlived the deadline by far more than the test allows."); }
        catch (Exception ex) { error = ex; }
        return (error, sw.Elapsed);
    }

    // ── CRL / OCSP ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false)] // CRL, the server never answers
    [InlineData(true, false)]  // CRL, headers at once, body dripping
    [InlineData(false, true)]  // OCSP, the server never answers
    [InlineData(true, true)]   // OCSP, body dripping
    public async Task A_slow_revocation_server_is_cut_at_the_operation_deadline(bool headersAtOnce, bool ocsp)
    {
        var url = new Uri($"https://crl.slow-{headersAtOnce}-{ocsp}.example.test/x");
        var retriever = Retriever(new SlowHandler(headersAtOnce), TimeSpan.FromSeconds(30));

        var (error, elapsed) = await UnderDeadline(TimeSpan.FromMilliseconds(300),
            () => retriever.Get(url, ocsp ? new byte[] { 1 } : null, null));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"took {elapsed}");
        Assert.False(RevocationFetchPolicy.TryGetCrl(url, out _), "a cut answer must not be cached");
    }

    [Fact]
    public async Task A_dripping_body_stops_at_its_own_read_limit_without_any_deadline()
    {
        var retriever = Retriever(new SlowHandler(headersAtOnce: true), TimeSpan.FromMilliseconds(250));
        var sw = Stopwatch.StartNew();
        var error = await Record.ExceptionAsync(() => Task.Run(
            () => retriever.Get(new Uri("https://crl.drip.example.test/x"), null, null)).WaitAsync(Bound));
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Many_slow_distribution_points_in_a_row_do_not_add_up_past_the_deadline()
    {
        // The operation has 20 fetches; each would take forever. The first cancelled fetch ends the work.
        var retriever = Retriever(new SlowHandler(headersAtOnce: false), TimeSpan.FromSeconds(30));
        var (error, elapsed) = await UnderDeadline(TimeSpan.FromMilliseconds(300), () =>
        {
            using (RevocationFetchPolicy.Begin())
            {
                for (var i = 0; i < 20; i++) retriever.Get(new Uri($"https://crl.many.example.test/{i}"), null, null);
            }
        });
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"took {elapsed}");
    }

    // ── TSA ─────────────────────────────────────────────────────────────

    private sealed class ExposedTsa(string host) : PinnedIpTsaClient($"https://{host}/tsr", null, null)
    {
        public byte[] Call() => GetTSAResponse(new byte[] { 1 });
    }

    private static HttpClient Fake(SlowHandler handler) => new(handler) { Timeout = Timeout.InfiniteTimeSpan };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_slow_timestamp_authority_ends_at_the_operation_deadline(bool headersAtOnce)
    {
        var savedClient = PinnedIpTsaClient.SharedClient;
        var savedTimeout = PinnedIpTsaClient.RequestTimeout;
        try
        {
            PinnedIpTsaClient.SharedClient = Fake(new SlowHandler(headersAtOnce));
            PinnedIpTsaClient.RequestTimeout = TimeSpan.FromSeconds(60); // longer than the deadline: the deadline decides
            var tsa = new ExposedTsa($"tsa-deadline-{headersAtOnce}.example.test");

            var (error, elapsed) = await UnderDeadline(TimeSpan.FromMilliseconds(300), () => tsa.Call());

            Assert.IsAssignableFrom<OperationCanceledException>(error); // our own deadline, not a fault of the authority
            Assert.True(elapsed < TimeSpan.FromSeconds(3), $"took {elapsed}");
            Assert.False(TsaCircuitBreaker.Default.IsOpen(tsa.BreakerKey), "our own deadline must not count against the authority");
        }
        finally
        {
            PinnedIpTsaClient.SharedClient = savedClient;
            PinnedIpTsaClient.RequestTimeout = savedTimeout;
        }
    }

    [Fact]
    public async Task A_timestamp_authority_that_does_not_answer_is_cut_at_its_own_timeout()
    {
        var savedClient = PinnedIpTsaClient.SharedClient;
        var savedTimeout = PinnedIpTsaClient.RequestTimeout;
        try
        {
            PinnedIpTsaClient.SharedClient = Fake(new SlowHandler(headersAtOnce: false));
            PinnedIpTsaClient.RequestTimeout = TimeSpan.FromMilliseconds(250);
            var tsa = new ExposedTsa("tsa-own-timeout.example.test");
            var sw = Stopwatch.StartNew();
            var error = await Record.ExceptionAsync(() => Task.Run(() => tsa.Call()).WaitAsync(Bound));
            Assert.IsType<TsaCommunicationException>(error);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
        }
        finally
        {
            PinnedIpTsaClient.SharedClient = savedClient;
            PinnedIpTsaClient.RequestTimeout = savedTimeout;
        }
    }

    // ── the client goes away ────────────────────────────────────────────

    [Fact]
    public async Task A_client_that_disconnects_stops_a_call_to_a_silent_authority()
    {
        var saved = PinnedIpTsaClient.SharedClient;
        try
        {
            PinnedIpTsaClient.SharedClient = Fake(new SlowHandler(headersAtOnce: false));
            var tsa = new ExposedTsa("tsa-disconnect.example.test");
            using var caller = new CancellationTokenSource();
            var gone = Task.Run(async () => { await Task.Delay(200); caller.Cancel(); });

            // An operation nobody can ask about again takes the request's own token.
            var (error, elapsed) = await UnderDeadline(TimeSpan.FromSeconds(60), () => tsa.Call(), caller.Token);
            await gone;

            Assert.NotNull(error);
            Assert.True(elapsed < TimeSpan.FromSeconds(3), $"took {elapsed}");
        }
        finally { PinnedIpTsaClient.SharedClient = saved; }
    }

    [Fact]
    public async Task A_keyed_operation_does_not_notice_the_client_leaving_only_the_deadline()
    {
        var saved = PinnedIpTsaClient.SharedClient;
        try
        {
            PinnedIpTsaClient.SharedClient = Fake(new SlowHandler(headersAtOnce: false));
            var tsa = new ExposedTsa("tsa-keyed.example.test");
            // What RunOperationAsync does for a retrievable operation: the caller's token is not passed on.
            var (error, elapsed) = await UnderDeadline(TimeSpan.FromMilliseconds(500), () => tsa.Call(), CancellationToken.None);

            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.True(elapsed >= TimeSpan.FromMilliseconds(400), $"ended early: {elapsed}");
        }
        finally { PinnedIpTsaClient.SharedClient = saved; }
    }
}
