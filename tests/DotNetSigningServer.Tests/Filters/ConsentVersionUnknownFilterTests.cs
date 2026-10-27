using DotNetSigningServer.Filters;
using DotNetSigningServer.Resources;
using DotNetSigningServer.Services.Consents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotNetSigningServer.Tests.Filters;

public class ConsentVersionUnknownFilterTests
{
    private static ExceptionContext Context(Exception ex) => new(
        new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), []) { Exception = ex };

    private static ConsentVersionUnknownFilter Filter()
    {
        var localizer = new Mock<IStringLocalizer<SharedStrings>>();
        localizer.Setup(l => l["ConsentVersionUnknown"]).Returns(new LocalizedString("ConsentVersionUnknown", "unknown"));
        return new ConsentVersionUnknownFilter(localizer.Object, NullLogger<ConsentVersionUnknownFilter>.Instance);
    }

    [Fact]
    public void UnknownVersion_Becomes503()
    {
        var context = Context(new ConsentVersionUnknownException("terms"));
        Filter().OnException(context);

        Assert.True(context.ExceptionHandled);
        Assert.Equal(503, Assert.IsType<ContentResult>(context.Result).StatusCode);
    }

    [Fact]
    public void OtherExceptions_AreLeftAlone()
    {
        var context = Context(new InvalidOperationException());
        Filter().OnException(context);

        Assert.False(context.ExceptionHandled);
        Assert.Null(context.Result);
    }
}
