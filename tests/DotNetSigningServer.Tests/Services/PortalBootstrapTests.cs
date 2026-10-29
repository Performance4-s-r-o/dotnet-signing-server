using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services;

public class PortalBootstrapValidatorTests
{
    private const string Good = "q7Hn2xV9mLp4RtYb8KcZ3wFd6JgSe1Ua";

    [Fact]
    public void Empty_is_off_and_valid()
    {
        Assert.Empty(PortalBootstrap.Validate(null));
        Assert.Empty(PortalBootstrap.Validate("  "));
    }

    [Fact]
    public void Strong_value_passes() => Assert.Empty(PortalBootstrap.Validate(Good));

    [Theory]
    [InlineData("short")]
    [InlineData("change-this-secret")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("has space inside the token value 1234567890")]
    public void Weak_values_are_refused(string value) => Assert.NotEmpty(PortalBootstrap.Validate(value));

    [Fact]
    public void Problems_never_contain_the_value()
    {
        const string weak = "weak-value-xyz";
        Assert.All(PortalBootstrap.Validate(weak), p => Assert.DoesNotContain(weak, p));
    }

    [Fact]
    public async Task Weak_value_stops_the_start_without_leaking_it()
    {
        await using var db = TestHelpers.CreateInMemoryDbContext();
        const string weak = "weak-value-xyz";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PortalBootstrap.EnsureAsync(
            new PortalBootstrapOptions { Token = weak }, new PrivateServerOptions(),
            TestHelpers.CreateTokenService(), db, NullLogger.Instance));
        Assert.DoesNotContain(weak, ex.Message);
    }

    [Fact]
    public void Hash_depends_on_the_secret_and_not_on_the_environment()
    {
        var a = TestHelpers.CreateTokenService("secret-one-long-enough-for-hmac-1").HashToken(Good);
        var b = TestHelpers.CreateTokenService("secret-one-long-enough-for-hmac-2").HashToken(Good);
        Assert.NotEqual(a, b);
        Assert.Equal(a, TestHelpers.CreateTokenService("secret-one-long-enough-for-hmac-1").HashToken(Good));
    }
}

[Trait("Category", "Db")]
public class PortalBootstrapPostgresTests : IClassFixture<OutboxPostgresFixture>
{
    private const string TokenA = "q7Hn2xV9mLp4RtYb8KcZ3wFd6JgSe1Ua";
    private const string TokenB = "Zk3Vb8Nc1Xm5Lp9Qw2Er7Ty4Ui6Oa0Sd";
    private const string SecretOne = "bootstrap-test-secret-one-0123456789";
    private const string SecretTwo = "bootstrap-test-secret-two-0123456789";

    private readonly OutboxPostgresFixture _pg;

    public PortalBootstrapPostgresTests(OutboxPostgresFixture pg) => _pg = pg;

    private async Task ResetAsync()
    {
        await using var db = _pg.CreateContext();
        await db.ApiTokens.Where(t => t.User!.Email == PortalBootstrap.AccountEmail).ExecuteDeleteAsync();
        await db.Users.Where(u => u.Email == PortalBootstrap.AccountEmail).ExecuteDeleteAsync();
    }

    private async Task RunAsync(string? token, string secret = SecretOne, bool privateServer = false)
    {
        await using var db = _pg.CreateContext();
        await PortalBootstrap.EnsureAsync(
            new PortalBootstrapOptions { Token = token },
            new PrivateServerOptions { Enabled = privateServer },
            TestHelpers.CreateTokenService(secret), db, NullLogger.Instance);
    }

    private async Task<(List<User> Users, List<ApiToken> Tokens)> StateAsync()
    {
        await using var db = _pg.CreateContext();
        var users = await db.Users.AsNoTracking().Where(u => u.Email == PortalBootstrap.AccountEmail).ToListAsync();
        var tokens = await db.ApiTokens.AsNoTracking().Where(t => t.User!.Email == PortalBootstrap.AccountEmail)
            .OrderBy(t => t.CreatedAt).ToListAsync();
        return (users, tokens);
    }

    [DockerFact]
    public async Task First_start_creates_the_service_account_and_token()
    {
        await ResetAsync();
        await RunAsync(TokenA, privateServer: true);

        var (users, tokens) = await StateAsync();
        var user = Assert.Single(users);
        Assert.True(user.IsServiceAccount);
        Assert.True(user.TrustedCaller);
        Assert.True(user.SealAllowed);
        Assert.True(user.IsEnterprise);
        Assert.False(user.EmailVerified);
        var token = Assert.Single(tokens);
        Assert.Equal(PortalBootstrap.TokenLabel, token.Label);
        Assert.Null(token.RevokedAt);
        Assert.Equal(TestHelpers.CreateTokenService(SecretOne).HashToken(TokenA), token.TokenHash);
        Assert.DoesNotContain(TokenA, Convert.ToBase64String(token.TokenHash));
    }

    [DockerFact]
    public async Task Without_private_server_the_account_is_not_enterprise()
    {
        await ResetAsync();
        await RunAsync(TokenA, privateServer: false);
        Assert.False(Assert.Single((await StateAsync()).Users).IsEnterprise);
    }

    [DockerFact]
    public async Task Second_start_changes_nothing()
    {
        await ResetAsync();
        await RunAsync(TokenA);
        var before = await StateAsync();
        await RunAsync(TokenA);
        var after = await StateAsync();

        Assert.Equal(before.Users.Single().Id, after.Users.Single().Id);
        Assert.Equal(before.Tokens.Single().Id, after.Tokens.Single().Id);
        Assert.Equal(before.Users.Single().UpdatedAt, after.Users.Single().UpdatedAt);
    }

    [DockerFact]
    public async Task Rotation_revokes_the_old_token_and_adds_the_new_one()
    {
        await ResetAsync();
        await RunAsync(TokenA);
        await RunAsync(TokenB);

        var (_, tokens) = await StateAsync();
        Assert.Equal(2, tokens.Count);
        Assert.NotNull(tokens[0].RevokedAt);
        Assert.Null(tokens[1].RevokedAt);
        Assert.Equal(TestHelpers.CreateTokenService(SecretOne).HashToken(TokenB), tokens[1].TokenHash);
    }

    [DockerFact]
    public async Task Changed_token_secret_gets_a_fresh_hash_and_retires_the_stale_one()
    {
        await ResetAsync();
        await RunAsync(TokenA, SecretOne);
        await RunAsync(TokenA, SecretTwo);

        var (users, tokens) = await StateAsync();
        Assert.Single(users);
        var active = Assert.Single(tokens, t => t.RevokedAt == null);
        Assert.Equal(TestHelpers.CreateTokenService(SecretTwo).HashToken(TokenA), active.TokenHash);
    }

    [DockerFact]
    public async Task Off_creates_nothing()
    {
        await ResetAsync();
        await RunAsync(null);
        await RunAsync("");
        var (users, tokens) = await StateAsync();
        Assert.Empty(users);
        Assert.Empty(tokens);
    }

    [DockerFact]
    public async Task An_ordinary_account_on_the_reserved_address_stops_the_start()
    {
        await ResetAsync();
        await using (var db = _pg.CreateContext())
        {
            db.Users.Add(new User { Email = PortalBootstrap.AccountEmail, IsActive = true });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(TokenA));
        await ResetAsync();
    }
}
