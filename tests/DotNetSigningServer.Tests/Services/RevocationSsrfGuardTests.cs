using System.Net;
using System.Net.Sockets;
using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Services;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// Long-term validation fetches revocation data from URLs printed inside a
/// certificate the caller supplied — the same outbound-request primitive the TSA URL
/// has, reached by a different route.
///
/// The pre-fetch check was already there; what these cover is the gap after it. DNS
/// is resolved once to validate, and without pinning the HTTP stack resolves again
/// when it connects, so a zero-TTL domain can answer public first and internal
/// second.
/// </summary>
public class RevocationSsrfGuardTests
{
    [Theory]
    [InlineData("http://127.0.0.1/ocsp")]
    [InlineData("http://localhost/crl")]
    [InlineData("http://169.254.169.254/latest/meta-data/")] // cloud instance metadata
    [InlineData("http://10.0.0.5/crl")]
    [InlineData("http://192.168.1.1/ocsp")]
    [InlineData("http://[::1]/ocsp")]
    public void IsFetchable_InternalTargets_Refused(string url)
    {
        Assert.False(RevocationEndpointGuard.IsFetchable(url));
    }

    [Theory]
    [InlineData("ftp://ocsp.example.com/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relative/path")]
    [InlineData("")]
    [InlineData(null)]
    public void IsFetchable_NonHttpTargets_Refused(string? url)
    {
        Assert.False(RevocationEndpointGuard.IsFetchable(url));
    }

    /// <summary>
    /// Unlike the TSA path, plain HTTP has to stay allowed: nearly every real
    /// certificate publishes OCSP and CRL over it, and the responses carry their own
    /// signatures, so the transport is not what is being trusted.
    /// </summary>
    [Fact]
    public void IsFetchable_PlainHttpToPublicHost_Allowed()
    {
        Assert.True(RevocationEndpointGuard.IsFetchable("http://ocsp.digicert.com"));
    }

    /// <summary>
    /// The retriever must refuse before opening a socket — returning null rather than
    /// throwing, because one bad distribution point should not abandon the others.
    /// </summary>
    [Fact]
    public void Retriever_InternalUrl_ReturnsNullWithoutConnecting()
    {
        var retriever = new GuardedResourceRetriever();

        Assert.Null(retriever.GetInputStreamByUrl(new Uri("http://169.254.169.254/latest/")));
        Assert.Null(retriever.GetByteArrayByUrl(new Uri("http://127.0.0.1/crl")));
        Assert.Null(retriever.Get(new Uri("http://10.0.0.1/ocsp"), new byte[] { 1 }, null));
    }

    /// <summary>
    /// The pinning itself: a listener is opened on loopback and the guarded client is
    /// pointed straight at it. Connecting would mean the guard never ran — the socket
    /// must be refused at connect time, which is exactly where a rebound DNS answer
    /// would land.
    /// </summary>
    [Fact]
    public async Task PinnedClient_HostResolvingToLoopback_RefusesAtConnectTime()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            using var client = PinnedIpHttp.CreateClient("REVOCATION_HOST_NOT_ALLOWED", TimeSpan.FromSeconds(5));

            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => client.GetAsync($"http://127.0.0.1:{port}/crl"));

            // HttpClient wraps connect-time failures, so the guard's own exception is
            // the inner one.
            var validation = error as ApiValidationException
                             ?? error.InnerException as ApiValidationException
                             ?? error.InnerException?.InnerException as ApiValidationException;

            Assert.NotNull(validation);
            Assert.Contains("REVOCATION_HOST_NOT_ALLOWED", validation!.Message);
            Assert.Equal(0, listener.Pending() ? 1 : 0); // nothing ever reached the listener
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Same client, same code path, but the caller names the TSA error — the refusal
    /// has to stay legible to whoever asked.
    /// </summary>
    [Fact]
    public async Task PinnedClient_CarriesCallerErrorCode()
    {
        using var client = PinnedIpHttp.CreateClient("TSA_HOST_NOT_ALLOWED", TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("http://127.0.0.1:9/tsr"));

        var validation = error as ApiValidationException
                         ?? error.InnerException as ApiValidationException
                         ?? error.InnerException?.InnerException as ApiValidationException;

        Assert.NotNull(validation);
        Assert.Contains("TSA_HOST_NOT_ALLOWED", validation!.Message);
    }
}
