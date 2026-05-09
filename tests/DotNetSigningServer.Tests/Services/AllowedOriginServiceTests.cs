using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services;

public class AllowedOriginServiceTests
{
    private static AllowedOriginService CreateService()
    {
        // Create a service provider with an InMemory DbContext for LoadAllowedOrigins
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<DotNetSigningServer.Data.ApplicationDbContext>(options =>
            Microsoft.EntityFrameworkCore.InMemoryDbContextOptionsExtensions.UseInMemoryDatabase(options, dbName));
        var sp = services.BuildServiceProvider();
        return new AllowedOriginService(sp.GetRequiredService<IServiceScopeFactory>());
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("https://localhost")]
    [InlineData("http://127.0.0.1")]
    [InlineData("https://127.0.0.1")]
    public void IsLocalOrigin_LocalAddresses_ReturnsTrue(string origin)
    {
        var sut = CreateService();
        Assert.True(sut.IsLocalOrigin(origin));
    }

    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("https://localhost:8443")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("https://127.0.0.1:443")]
    public void IsLocalOrigin_LocalWithPort_ReturnsTrue(string origin)
    {
        var sut = CreateService();
        Assert.True(sut.IsLocalOrigin(origin));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://my-app.azurewebsites.net")]
    [InlineData("http://192.168.1.1")]
    public void IsLocalOrigin_RemoteOrigins_ReturnsFalse(string origin)
    {
        var sut = CreateService();
        Assert.False(sut.IsLocalOrigin(origin));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    public void IsLocalOrigin_InvalidOrigin_ReturnsFalse(string origin)
    {
        var sut = CreateService();
        Assert.False(sut.IsLocalOrigin(origin));
    }

    [Fact]
    public void IsOriginAllowedForToken_LocalOrigin_AlwaysAllowed()
    {
        var sut = CreateService();
        var token = new ApiToken
        {
            IsBrowserToken = true,
            AllowedOrigins = "https://example.com"
        };
        Assert.True(sut.IsOriginAllowedForToken("http://localhost:3000", token));
    }

    [Fact]
    public void IsOriginAllowedForToken_MatchingOrigin_ReturnsTrue()
    {
        var sut = CreateService();
        var token = new ApiToken
        {
            IsBrowserToken = true,
            AllowedOrigins = "https://example.com"
        };
        Assert.True(sut.IsOriginAllowedForToken("https://example.com", token));
    }

    [Fact]
    public void IsOriginAllowedForToken_NonMatchingOrigin_ReturnsFalse()
    {
        var sut = CreateService();
        var token = new ApiToken
        {
            IsBrowserToken = true,
            AllowedOrigins = "https://example.com"
        };
        Assert.False(sut.IsOriginAllowedForToken("https://other.com", token));
    }

    [Fact]
    public void IsOriginAllowedForToken_NullOrigin_ReturnsFalse()
    {
        var sut = CreateService();
        var token = new ApiToken
        {
            IsBrowserToken = true,
            AllowedOrigins = "https://example.com"
        };
        // NormalizeOrigin returns null for empty → returns false for browser token
        Assert.False(sut.IsOriginAllowedForToken("", token));
    }

    [Fact]
    public void IsOriginAllowedForToken_MultipleOrigins_MatchesAny()
    {
        var sut = CreateService();
        var token = new ApiToken
        {
            IsBrowserToken = true,
            AllowedOrigins = "https://one.com, https://two.com; https://three.com"
        };
        Assert.True(sut.IsOriginAllowedForToken("https://two.com", token));
    }

    [Fact]
    public void IsOriginAllowedForToken_HttpOriginNonLocal_Rejected()
    {
        // ParseOrigins filters out non-https non-local origins
        var sut = CreateService();
        var token = new ApiToken
        {
            IsBrowserToken = true,
            AllowedOrigins = "http://example.com"
        };
        Assert.False(sut.IsOriginAllowedForToken("http://example.com", token));
    }

    [Fact]
    public void IsOriginAllowedForToken_EmptyAllowedOrigins_OnlyLocalAllowed()
    {
        var sut = CreateService();
        var token = new ApiToken
        {
            IsBrowserToken = true,
            AllowedOrigins = null
        };
        Assert.False(sut.IsOriginAllowedForToken("https://example.com", token));
        Assert.True(sut.IsOriginAllowedForToken("http://localhost", token));
    }
}
