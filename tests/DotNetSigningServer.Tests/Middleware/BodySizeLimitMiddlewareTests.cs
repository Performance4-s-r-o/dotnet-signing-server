using DotNetSigningServer.Middleware;
using DotNetSigningServer.Options;
using DotNetSigningServer.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using DotNetSigningServer.Tests.Helpers;
using Moq;
using Xunit;

namespace DotNetSigningServer.Tests.Middleware;

public class BodySizeLimitMiddlewareTests
{
    private static async Task<(int Status, bool Reached)> Run(string path, long contentLength)
    {
        var reached = false;
        var middleware = new BodySizeLimitMiddleware(
            _ => { reached = true; return Task.CompletedTask; },
            TestHelpers.WrapOptions(new LimitsOptions()),
            TestHelpers.WrapOptions(new RateLimitOptions()),
            NullLogger<BodySizeLimitMiddleware>.Instance);

        var localizer = new Mock<IStringLocalizer>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string n) => new LocalizedString(n, n));
        var factory = new Mock<IStringLocalizerFactory>();
        factory.Setup(f => f.Create(typeof(SharedStrings))).Returns(localizer.Object);

        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(factory.Object).BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();
        context.Request.Path = path;
        context.Request.ContentLength = contentLength;

        await middleware.InvokeAsync(context);
        return (context.Response.StatusCode, reached);
    }

    [Fact]
    public async Task Seal_accepts_a_few_bytes_and_refuses_a_document()
    {
        Assert.True((await Run("/api/seal", 500)).Reached);

        var (status, reached) = await Run("/api/seal", 5 * 1024 * 1024);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, status);
        Assert.False(reached);
    }

    [Fact]
    public async Task Other_api_routes_keep_the_document_limit()
    {
        Assert.True((await Run("/api/sign-pfx", 5 * 1024 * 1024)).Reached);
    }
}
