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
    private const string Good = "q7Hn2xV9mLp4RtYb8KcZ3wFd6JgSe1UaP0oIuY5tR4e=";

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
    [InlineData("{{project.DOTNET_PORTAL_BOOTSTRAP_TOKEN}}")]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123")]
    [InlineData("Ab3dEf7hIj1mNo5qRs9uVw2yZa6Ab3dEf7hIj1mNo5qRs9uVw2yZa6")]
    [InlineData("q7hn2xv9mlp4rtyb8kcz3wfd6jgse1uaq7hn2xv9mlp4rtyb8kcz")]
    public void Weak_values_are_refused(string value) => Assert.NotEmpty(PortalBootstrap.Validate(value));

    [Fact]
    public void Problems_never_contain_the_value()
    {
        const string weak = "weak-value-xyz";
        Assert.All(PortalBootstrap.Validate(weak), p => Assert.DoesNotContain(weak, p));
    }

    [Fact]
    public void Long_hex_passes() =>
        Assert.Empty(PortalBootstrap.Validate("3fa91c07be5d42e8a6c1d0f94b72e385c6a1f09d7b34e82a5c90d1f6b7e3a428"));

    [Fact]
    public void Reserved_addresses_are_recognised_in_any_case()
    {
        Assert.True(UserEmails.IsReserved("portal@service.invalid"));
        Assert.True(UserEmails.IsReserved(" PORTAL@Service.Invalid "));
        Assert.True(UserEmails.IsReserved("x@invalid"));
        Assert.False(UserEmails.IsReserved("x@example.com"));
    }

    [Fact]
    public async Task Weak_value_stops_the_start_without_leaking_it()
    {
        await using var db = TestHelpers.CreateInMemoryDbContext();
        const string weak = "weak-value-xyz";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PortalBootstrap.EnsureAsync(
            new PortalBootstrapOptions { Token = weak }, TestHelpers.CreateTokenService(), new IpWhitelistService(), db, NullLogger.Instance));
        Assert.DoesNotContain(weak, ex.Message);
    }

    [Theory]
    [InlineData(Good + "," + Good)]
    [InlineData(Good + ",Zk3Vb8Nc1Xm5Lp9Qw2Er7Ty4Ui6Oa0SdH7gF2dS9a1Kx3Mn=,Xp4Lm8Nq2Wr6Ty0Ui3Oa7Sd1Fg5Hj9Kl2Zx4Cv8Bn=")]
    public async Task A_repeated_value_or_more_than_two_stop_the_start(string token)
    {
        await using var db = TestHelpers.CreateInMemoryDbContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PortalBootstrap.EnsureAsync(
            new PortalBootstrapOptions { Token = token }, TestHelpers.CreateTokenService(), new IpWhitelistService(), db, NullLogger.Instance));
    }

    [Fact]
    public async Task Allowed_ips_that_do_not_parse_stop_the_start()
    {
        await using var db = TestHelpers.CreateInMemoryDbContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PortalBootstrap.EnsureAsync(
            new PortalBootstrapOptions { Token = Good, AllowedIps = "10.0.0.1,not-an-ip" },
            TestHelpers.CreateTokenService(), new IpWhitelistService(), db, NullLogger.Instance));
    }

    [Fact]
    public void A_list_is_split_on_commas_and_trimmed()
    {
        Assert.Equal(new[] { "a", "b" }, new PortalBootstrapOptions { Token = " a , b ," }.Values());
        Assert.Empty(new PortalBootstrapOptions { Token = "  " }.Values());
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
    private const string TokenA = "q7Hn2xV9mLp4RtYb8KcZ3wFd6JgSe1UaP0oIuY5tR4e=";
    private const string TokenB = "Zk3Vb8Nc1Xm5Lp9Qw2Er7Ty4Ui6Oa0SdH7gF2dS9a1Kx3Mn=";
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

    private async Task RunAsync(string? token, string secret = SecretOne, string? allowedIps = null)
    {
        await using var db = _pg.CreateContext();
        await PortalBootstrap.EnsureAsync(
            new PortalBootstrapOptions { Token = token, AllowedIps = allowedIps },
            TestHelpers.CreateTokenService(secret), new IpWhitelistService(), db, NullLogger.Instance);
    }

    private async Task<(List<User> Users, List<ApiToken> Tokens)> StateAsync()
    {
        await using var db = _pg.CreateContext();
        var users = await db.Users.AsNoTracking().Where(u => u.Email == PortalBootstrap.AccountEmail).ToListAsync();
        var tokens = await db.ApiTokens.AsNoTracking().Where(t => t.User!.Email == PortalBootstrap.AccountEmail)
            .OrderBy(t => t.CreatedAt).ToListAsync();
        return (users, tokens);
    }

    private static byte[] Hash(string token, string secret = SecretOne) => TestHelpers.CreateTokenService(secret).HashToken(token);

    [DockerFact]
    public async Task First_start_creates_an_enterprise_service_account_with_a_provisioning_token()
    {
        await ResetAsync();
        await RunAsync(TokenA);

        var (users, tokens) = await StateAsync();
        var user = Assert.Single(users);
        Assert.True(user.IsServiceAccount);
        Assert.True(user.SealAllowed);
        // Enterprise also in SaaS (Q1): provisioned accounts inherit it.
        Assert.True(user.IsEnterprise);
        Assert.NotNull(user.EnterpriseEnabledAt);
        // Inherited by provisioned accounts that set none of their own.
        Assert.Equal(PortalBootstrap.ProvisionedConcurrencyLimit, user.MaxConcurrentOperations);
        Assert.Equal(PortalBootstrap.ProvisionedQueueSeconds, user.ConcurrencyQueueTimeoutSeconds);
        Assert.False(user.EmailVerified);
        var token = Assert.Single(tokens);
        Assert.Equal(PortalBootstrap.TokenLabel, token.Label);
        Assert.True(token.IsProvisioningToken);
        Assert.False(token.IsBrowserToken);
        Assert.Null(token.RevokedAt);
        Assert.Null(token.AllowedIps);
        Assert.Equal(Hash(TokenA), token.TokenHash);
        Assert.DoesNotContain(TokenA, Convert.ToBase64String(token.TokenHash));
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
    public async Task Rotation_keeps_both_values_active_and_revokes_the_one_that_left_the_list()
    {
        await ResetAsync();
        await RunAsync(TokenA);
        await RunAsync($"{TokenB},{TokenA}");

        var (_, both) = await StateAsync();
        Assert.Equal(2, both.Count);
        Assert.All(both, t => Assert.Null(t.RevokedAt));
        Assert.All(both, t => Assert.True(t.IsProvisioningToken));

        await RunAsync(TokenB);
        var (_, tokens) = await StateAsync();
        Assert.Equal(2, tokens.Count);
        Assert.NotNull(Assert.Single(tokens, t => t.TokenHash.SequenceEqual(Hash(TokenA))).RevokedAt);
        Assert.Null(Assert.Single(tokens, t => t.TokenHash.SequenceEqual(Hash(TokenB))).RevokedAt);
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
        Assert.Equal(Hash(TokenA, SecretTwo), active.TokenHash);
    }

    [DockerFact]
    public async Task Allowed_ips_are_written_on_the_token_and_follow_the_configuration()
    {
        await ResetAsync();
        await RunAsync(TokenA, allowedIps: "10.0.0.5, 172.18.0.0/16");
        Assert.Equal("10.0.0.5,172.18.0.0/16", Assert.Single((await StateAsync()).Tokens).AllowedIps);

        await RunAsync(TokenA);
        Assert.Null(Assert.Single((await StateAsync()).Tokens).AllowedIps);
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

    [DockerFact]
    public async Task A_value_that_belongs_to_another_account_stops_the_start()
    {
        await ResetAsync();
        var hash = Hash(TokenA);
        Guid manualId;
        await using (var db = _pg.CreateContext())
        {
            var manual = new User { Email = $"manual-{Guid.NewGuid():N}@example.com", IsActive = true };
            db.Users.Add(manual);
            db.ApiTokens.Add(new ApiToken { UserId = manual.Id, Label = "hand", TokenHash = hash });
            await db.SaveChangesAsync();
            manualId = manual.Id;
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(TokenA));
        Assert.DoesNotContain(TokenA, ex.Message);

        var (users, tokens) = await StateAsync();
        Assert.Empty(users);
        Assert.Empty(tokens);
        await using var check = _pg.CreateContext();
        Assert.False(Assert.Single(await check.ApiTokens.AsNoTracking().Where(t => t.TokenHash == hash).ToListAsync()).IsProvisioningToken);
        await check.ApiTokens.Where(t => t.UserId == manualId).ExecuteDeleteAsync();
        await check.Users.Where(u => u.Id == manualId).ExecuteDeleteAsync();
    }

    [DockerFact]
    public async Task A_revoked_value_is_not_revived()
    {
        await ResetAsync();
        await RunAsync(TokenA);
        await RunAsync(TokenB);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(TokenA));
        Assert.DoesNotContain(TokenA, ex.Message);
        Assert.Single((await StateAsync()).Tokens, t => t.RevokedAt == null);
    }

    [DockerFact]
    public async Task A_reserved_address_in_other_letter_case_stops_the_start()
    {
        await ResetAsync();
        await using (var db = _pg.CreateContext())
        {
            db.Users.Add(new User { Email = "PORTAL@service.invalid", IsActive = true });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(TokenA));
        await using var cleanup = _pg.CreateContext();
        await cleanup.Users.Where(u => u.Email == "PORTAL@service.invalid").ExecuteDeleteAsync();
    }

    [DockerFact]
    public async Task A_deactivated_service_account_stops_the_start()
    {
        await ResetAsync();
        await RunAsync(TokenA);
        await using (var db = _pg.CreateContext())
        {
            await db.Users.Where(u => u.Email == PortalBootstrap.AccountEmail)
                .ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, false));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(TokenB));
        Assert.Single((await StateAsync()).Tokens);

        await using (var db = _pg.CreateContext())
        {
            await db.Users.Where(u => u.Email == PortalBootstrap.AccountEmail)
                .ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, true).SetProperty(u => u.ErasedAt, DateTimeOffset.UtcNow));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(TokenB));
    }

    [DockerFact]
    public async Task The_token_stores_no_prefix()
    {
        await ResetAsync();
        await RunAsync(TokenA);
        Assert.Null(Assert.Single((await StateAsync()).Tokens).TokenPrefix);
    }

    [DockerFact]
    public async Task A_plain_token_of_the_service_account_with_the_value_becomes_the_provisioning_token()
    {
        await ResetAsync();
        await RunAsync(TokenA);
        await using (var db = _pg.CreateContext())
        {
            await db.ApiTokens.Where(t => t.User!.Email == PortalBootstrap.AccountEmail)
                .ExecuteUpdateAsync(x => x.SetProperty(t => t.Label, "manual").SetProperty(t => t.IsProvisioningToken, false));
        }

        await RunAsync(TokenA);

        var token = Assert.Single((await StateAsync()).Tokens);
        Assert.True(token.IsProvisioningToken);
        Assert.Equal(PortalBootstrap.TokenLabel, token.Label);
    }
}
