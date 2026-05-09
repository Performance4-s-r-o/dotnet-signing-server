using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services;

public class ApiAuthServiceTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly TokenService _tokenService;
    private readonly AllowedOriginService _allowedOriginService;
    private readonly IpWhitelistService _ipWhitelistService;
    private readonly ApiAuthService _sut;
    private readonly string _dbName = Guid.NewGuid().ToString();

    public ApiAuthServiceTests()
    {
        var dbOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options;
        _dbContext = new ApplicationDbContext(dbOptions);
        _dbContext.Database.EnsureCreated();

        _tokenService = TestHelpers.CreateTokenService("test-secret-long-enough-for-hmac");

        // Create a service provider with same InMemory DB for AllowedOriginService
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options =>
            Microsoft.EntityFrameworkCore.InMemoryDbContextOptionsExtensions.UseInMemoryDatabase(options, _dbName));
        var sp = services.BuildServiceProvider();
        _allowedOriginService = new AllowedOriginService(sp.GetRequiredService<IServiceScopeFactory>());
        _ipWhitelistService = new IpWhitelistService();

        _sut = new ApiAuthService(_dbContext, _tokenService, _allowedOriginService, _ipWhitelistService, new MemoryCache(new MemoryCacheOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<ApiAuthService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    private (User User, string PlaintextToken, ApiToken ApiToken) SeedUserWithToken(
        bool isBrowserToken = false,
        string? allowedOrigins = null,
        string? allowedIps = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? revokedAt = null)
    {
        var user = TestHelpers.CreateTestUser($"user-{Guid.NewGuid()}@test.com");
        _dbContext.Users.Add(user);

        var (token, hash, _) = _tokenService.IssueToken(user, "test-token", expiresAt);
        var apiToken = new ApiToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Label = "test",
            TokenHash = hash,
            IsBrowserToken = isBrowserToken,
            AllowedOrigins = allowedOrigins,
            AllowedIps = allowedIps,
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _dbContext.ApiTokens.Add(apiToken);
        _dbContext.SaveChanges();

        return (user, token, apiToken);
    }

    [Fact]
    public async Task ValidateTokenAsync_EmptyHeader_ReturnsNull()
    {
        var result = await _sut.ValidateTokenAsync("");
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateTokenAsync_NoBearer_ReturnsNull()
    {
        var result = await _sut.ValidateTokenAsync("Basic abc123");
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateTokenAsync_BearerWithEmptyToken_ReturnsNull()
    {
        var result = await _sut.ValidateTokenAsync("Bearer ");
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateTokenAsync_InvalidToken_ReturnsNull()
    {
        SeedUserWithToken();
        var result = await _sut.ValidateTokenAsync("Bearer not-a-valid-token");
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateTokenAsync_ValidServerToken_ReturnsUser()
    {
        var (user, token, _) = SeedUserWithToken();
        var result = await _sut.ValidateTokenAsync($"Bearer {token}");
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    [Fact]
    public async Task ValidateTokenAsync_ExpiredToken_ReturnsNull()
    {
        var (_, token, _) = SeedUserWithToken(expiresAt: DateTimeOffset.UtcNow.AddHours(-1));
        var result = await _sut.ValidateTokenAsync($"Bearer {token}");
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateTokenAsync_RevokedToken_ReturnsNull()
    {
        var (_, token, _) = SeedUserWithToken(revokedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var result = await _sut.ValidateTokenAsync($"Bearer {token}");
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateTokenAsync_BrowserToken_WithMatchingOrigin_ReturnsUser()
    {
        var (user, token, _) = SeedUserWithToken(
            isBrowserToken: true,
            allowedOrigins: "https://myapp.com");
        var result = await _sut.ValidateTokenAsync($"Bearer {token}", "https://myapp.com");
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    [Fact]
    public async Task ValidateTokenAsync_BrowserToken_WithoutOrigin_ReturnsNull()
    {
        var (_, token, _) = SeedUserWithToken(
            isBrowserToken: true,
            allowedOrigins: "https://myapp.com");
        var result = await _sut.ValidateTokenAsync($"Bearer {token}");
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateTokenAsync_BrowserToken_WithWrongOrigin_ReturnsNull()
    {
        var (_, token, _) = SeedUserWithToken(
            isBrowserToken: true,
            allowedOrigins: "https://myapp.com");
        var result = await _sut.ValidateTokenAsync($"Bearer {token}", "https://evil.com");
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateTokenAsync_BrowserToken_LocalOrigin_AllowedAlways()
    {
        var (user, token, _) = SeedUserWithToken(
            isBrowserToken: true,
            allowedOrigins: "https://myapp.com");
        var result = await _sut.ValidateTokenAsync($"Bearer {token}", "http://localhost:3000");
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    [Fact]
    public async Task ValidateTokenAsync_ServerToken_WithRemoteOrigin_ReturnsNull()
    {
        var (_, token, _) = SeedUserWithToken(isBrowserToken: false);
        // Server token + non-local origin → rejected
        var result = await _sut.ValidateTokenAsync($"Bearer {token}", "https://some-site.com");
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateTokenAsync_ServerToken_WithLocalOrigin_ReturnsUser()
    {
        var (user, token, _) = SeedUserWithToken(isBrowserToken: false);
        var result = await _sut.ValidateTokenAsync($"Bearer {token}", "http://localhost");
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    [Fact]
    public async Task ValidateTokenAsync_ServerToken_WithNoOrigin_ReturnsUser()
    {
        var (user, token, _) = SeedUserWithToken(isBrowserToken: false);
        var result = await _sut.ValidateTokenAsync($"Bearer {token}");
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    [Fact]
    public async Task ValidateTokenAsync_NormalizesSpacesToPlus()
    {
        var (user, token, _) = SeedUserWithToken();
        // Simulate copy/paste replacing + with space
        var mangled = token.Replace('+', ' ');
        var result = await _sut.ValidateTokenAsync($"Bearer {mangled}");
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    [Fact]
    public async Task ValidateTokenAsync_FixesMissingBase64Padding()
    {
        var (user, token, _) = SeedUserWithToken();
        // Remove trailing = padding
        var trimmed = token.TrimEnd('=');
        var result = await _sut.ValidateTokenAsync($"Bearer {trimmed}");
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    // --- IP Whitelisting Tests ---

    [Fact]
    public async Task ValidateTokenAsync_ServerToken_NoAllowedIps_AnyIpAllowed()
    {
        var (user, token, _) = SeedUserWithToken(isBrowserToken: false, allowedIps: null);
        var result = await _sut.ValidateTokenAsync($"Bearer {token}", null, System.Net.IPAddress.Parse("8.8.8.8"));
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    [Fact]
    public async Task ValidateTokenAsync_ServerToken_AllowedIp_Matches_ReturnsUser()
    {
        var (user, token, _) = SeedUserWithToken(isBrowserToken: false, allowedIps: "10.0.0.1");
        var result = await _sut.ValidateTokenAsync($"Bearer {token}", null, System.Net.IPAddress.Parse("10.0.0.1"));
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    [Fact]
    public async Task ValidateTokenAsync_ServerToken_AllowedIp_NoMatch_ReturnsNull()
    {
        var (_, token, _) = SeedUserWithToken(isBrowserToken: false, allowedIps: "10.0.0.1");
        var result = await _sut.ValidateTokenAsync($"Bearer {token}", null, System.Net.IPAddress.Parse("10.0.0.2"));
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateTokenAsync_ServerToken_CidrAllowed_ReturnsUser()
    {
        var (user, token, _) = SeedUserWithToken(isBrowserToken: false, allowedIps: "192.168.0.0/16");
        var result = await _sut.ValidateTokenAsync($"Bearer {token}", null, System.Net.IPAddress.Parse("192.168.1.50"));
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    [Fact]
    public async Task ValidateTokenAsync_ServerToken_IPv4MappedIPv6_MatchesIPv4()
    {
        var (user, token, _) = SeedUserWithToken(isBrowserToken: false, allowedIps: "10.0.0.1");
        var mappedIp = System.Net.IPAddress.Parse("10.0.0.1").MapToIPv6();
        var result = await _sut.ValidateTokenAsync($"Bearer {token}", null, mappedIp);
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    [Fact]
    public async Task ValidateTokenAsync_ServerToken_AllowedIps_NullClientIp_ReturnsNull()
    {
        var (_, token, _) = SeedUserWithToken(isBrowserToken: false, allowedIps: "10.0.0.1");
        var result = await _sut.ValidateTokenAsync($"Bearer {token}", null, null);
        Assert.Null(result);
    }
}
