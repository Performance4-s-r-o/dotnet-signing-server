using System.Net;
using System.Text;
using DotNetSigningServer.Services.Settings;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services.Settings;

/// <summary>Runtime settings (dotnet #174): resolution order, kill switches, the client.</summary>
public class RuntimeSettingsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private static string Body(long version, string items, int stale = 300) =>
        $$"""{"version":{{version}},"poll_interval_seconds":30,"closed_max_stale_seconds":{{stale}},"items":[{{items}}]}""";

    private const string AiOn = """{"key":"ai_enabled","type":"bool","value":true,"default":false,"fail_mode":"closed"}""";
    private const string AiOff = """{"key":"ai_enabled","type":"bool","value":false,"default":false,"fail_mode":"closed"}""";
    private const string Templates7 = """{"key":"max_templates_per_user","type":"int","value":7,"default":100,"fail_mode":"last_known"}""";
    private const string TemplatesDefault = """{"key":"max_templates_per_user","type":"int","value":null,"default":50,"fail_mode":"last_known"}""";

    private static (RuntimeSettings Settings, ManualTimeProvider Time) Create(params (string, string)[] env)
    {
        var time = new ManualTimeProvider(T0);
        var overrides = RuntimeSettingsOverrides.FromValues(env.ToDictionary(e => e.Item1, e => (string?)e.Item2));
        return (new RuntimeSettings(overrides, time, NullLogger<RuntimeSettings>.Instance), time);
    }

    [Fact]
    public void Before_the_first_read_the_defaults_in_code_apply_and_AI_is_closed()
    {
        var (settings, _) = Create();
        Assert.Equal(20, settings.MaxPendingPresignsPerUser());
        Assert.Equal(100, settings.MaxTemplatesPerUser());
        Assert.Equal(100, settings.MaxApiTokensPerUser());
        Assert.Equal(TimeSpan.FromMinutes(15), settings.ReauthWindow());
        Assert.Equal(5, settings.OtpMaxFailedAttempts());
        Assert.Equal(30, settings.AccountMaxAttempts());
        Assert.Equal(TimeSpan.FromMinutes(15), settings.AccountWindow());
        Assert.False(settings.AiEnabled());
    }

    [Fact]
    public void The_service_value_wins_over_the_default_and_null_means_its_default()
    {
        var (settings, _) = Create();
        settings.Apply(RuntimeSettingsSnapshot.Parse(Body(1, Templates7)), T0);
        Assert.Equal(7, settings.MaxTemplatesPerUser());

        settings.Apply(RuntimeSettingsSnapshot.Parse(Body(2, TemplatesDefault)), T0);
        Assert.Equal(50, settings.MaxTemplatesPerUser());
    }

    [Fact]
    public void Env_wins_for_last_known_keys_and_may_switch_a_limit_off()
    {
        var (settings, _) = Create(("MAX_TEMPLATES_PER_USER", "0"), ("AUTH_REAUTH_MINUTES", "5"));
        settings.Apply(RuntimeSettingsSnapshot.Parse(Body(1, Templates7)), T0);
        Assert.Equal(0, settings.MaxTemplatesPerUser());
        Assert.Equal(TimeSpan.FromMinutes(5), settings.ReauthWindow());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public void An_unreadable_env_override_stops_the_start(string raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RuntimeSettingsOverrides.FromValues(new Dictionary<string, string?> { ["MAX_API_TOKENS_PER_USER"] = raw }));
        Assert.Contains("MAX_API_TOKENS_PER_USER", ex.Message);
    }

    [Fact]
    public void Empty_or_placeholder_env_is_no_override()
    {
        var overrides = RuntimeSettingsOverrides.FromValues(new Dictionary<string, string?>
        {
            ["MAX_API_TOKENS_PER_USER"] = "",
            ["MAX_TEMPLATES_PER_USER"] = "{{environment.DOTNET_MAX_TEMPLATES_PER_USER}}",
        });
        Assert.False(overrides.TryGetInt("max_api_tokens_per_user", out _));
        Assert.False(overrides.TryGetInt("max_templates_per_user", out _));
    }

    [Fact]
    public void A_kill_switch_is_on_only_while_the_service_said_so_recently()
    {
        var (settings, time) = Create();
        settings.Apply(RuntimeSettingsSnapshot.Parse(Body(1, AiOn)), T0);
        Assert.True(settings.AiEnabled());

        time.Advance(TimeSpan.FromSeconds(300));
        Assert.True(settings.AiEnabled());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(settings.AiEnabled());

        // A 304 counts as a successful read: fresh again.
        settings.MarkNotModified(time.GetUtcNow());
        Assert.True(settings.AiEnabled());
    }

    [Fact]
    public void A_kill_switch_follows_the_stale_limit_of_the_service()
    {
        var (settings, time) = Create();
        settings.Apply(RuntimeSettingsSnapshot.Parse(Body(1, AiOn, stale: 60)), T0);
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(settings.AiEnabled());
    }

    [Fact]
    public void A_kill_switch_missing_from_the_service_is_closed()
    {
        var (settings, _) = Create();
        settings.Apply(RuntimeSettingsSnapshot.Parse(Body(1, Templates7)), T0);
        Assert.False(settings.AiEnabled());
    }

    [Fact]
    public void Env_can_only_close_a_kill_switch()
    {
        var (closed, _) = Create(("AI_ENABLED", "false"));
        closed.Apply(RuntimeSettingsSnapshot.Parse(Body(1, AiOn)), T0);
        Assert.False(closed.AiEnabled());

        var overrides = RuntimeSettingsOverrides.FromValues(new Dictionary<string, string?> { ["AI_ENABLED"] = "true" });
        Assert.Single(overrides.Ignored);
        var (opened, _) = Create(("AI_ENABLED", "true"));
        Assert.False(opened.AiEnabled()); // no service value: closed, whatever env says
        opened.Apply(RuntimeSettingsSnapshot.Parse(Body(1, AiOff)), T0);
        Assert.False(opened.AiEnabled());
    }

    [Fact]
    public void An_older_version_is_not_taken()
    {
        var (settings, _) = Create();
        settings.Apply(RuntimeSettingsSnapshot.Parse(Body(5, Templates7)), T0);
        settings.Apply(RuntimeSettingsSnapshot.Parse(Body(4, TemplatesDefault)), T0);
        Assert.Equal(7, settings.MaxTemplatesPerUser());
        Assert.Equal(5, settings.Version);
    }

    // ── Client ──

    private sealed class Host : IDisposable
    {
        public StubServiceHandler Service { get; } = new();
        public ManualTimeProvider Time { get; } = new(T0);
        public ServiceProvider Services { get; }

        public Host()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            DotNetSigningServer.Services.Backoffice.BackofficeRegistration.AddRuntimeSettings(services, new DotNetSigningServer.Options.P4BackofficeProductOptions
            {
                BaseUrl = OutboxTestHost.BaseUrl,
                SecretKey = OutboxTestHost.SecretKey,
            });
            services.AddHttpClient(RuntimeSettingsClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Service);
            Services = services.BuildServiceProvider();
        }

        public RuntimeSettingsClient Client => Services.GetRequiredService<RuntimeSettingsClient>();
        public RuntimeSettings Settings => Services.GetRequiredService<RuntimeSettings>();

        public void Dispose() => Services.Dispose();
    }

    [Fact]
    public async Task Client_sends_the_etag_and_the_boot_version_and_keeps_the_last_copy_on_errors()
    {
        using var host = new Host();
        host.Service.Enqueue(HttpStatusCode.OK, Body(3, AiOn + "," + Templates7));
        host.Service.Enqueue(HttpStatusCode.NotModified);
        host.Service.Enqueue(HttpStatusCode.ServiceUnavailable);

        await host.Client.RefreshAsync(CancellationToken.None);
        Assert.Equal(7, host.Settings.MaxTemplatesPerUser());
        Assert.True(host.Settings.AiEnabled());
        var first = host.Service.Requests[0].Request;
        Assert.Equal("v1/settings", first.RequestUri!.ToString().Replace(OutboxTestHost.BaseUrl, ""));
        Assert.Empty(first.Headers.IfNoneMatch);
        Assert.False(first.Headers.Contains("P4-Settings-Boot-Version"));
        Assert.Null(first.RequestUri.Query is { Length: > 0 } q ? q : null); // no ?product=: the key decides

        host.Time.Advance(TimeSpan.FromSeconds(200));
        await host.Client.RefreshAsync(CancellationToken.None);
        var second = host.Service.Requests[1].Request;
        Assert.Equal("\"3\"", Assert.Single(second.Headers.IfNoneMatch).ToString());
        Assert.Equal("3", Assert.Single(second.Headers.GetValues("P4-Settings-Boot-Version")));

        // The 304 kept the switch fresh; the 503 keeps the values, and the switch closes only once stale.
        host.Time.Advance(TimeSpan.FromSeconds(200));
        await host.Client.RefreshAsync(CancellationToken.None);
        Assert.Equal(7, host.Settings.MaxTemplatesPerUser());
        Assert.True(host.Settings.AiEnabled());
        host.Time.Advance(TimeSpan.FromSeconds(101));
        Assert.False(host.Settings.AiEnabled());
        Assert.Equal(7, host.Settings.MaxTemplatesPerUser());
    }

    [Fact]
    public async Task A_refresh_during_a_running_read_joins_it_and_reads_once_more()
    {
        using var host = new Host();
        var release = new TaskCompletionSource();
        host.Service.Responses.Enqueue(async _ =>
        {
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body(1, AiOff), Encoding.UTF8, "application/json") };
        });
        // The change that triggered the second request is only in the second read.
        host.Service.Enqueue(HttpStatusCode.OK, Body(2, AiOn));

        var a = host.Client.RefreshAsync(CancellationToken.None);
        var b = host.Client.RefreshAsync(CancellationToken.None);
        var c = host.Client.RefreshAsync(CancellationToken.None);
        release.SetResult();
        await Task.WhenAll(a, b, c);

        Assert.Equal(2, host.Service.Requests.Count);
        Assert.Equal(2, host.Settings.Version);
        Assert.True(host.Settings.AiEnabled());
    }

    [Fact]
    public async Task A_garbled_answer_keeps_what_is_known()
    {
        using var host = new Host();
        host.Service.Enqueue(HttpStatusCode.OK, Body(1, Templates7));
        host.Service.Enqueue(HttpStatusCode.OK, "<html>proxy</html>");
        await host.Client.RefreshAsync(CancellationToken.None);
        await host.Client.RefreshAsync(CancellationToken.None);
        Assert.Equal(7, host.Settings.MaxTemplatesPerUser());
    }

    [Fact]
    public void Without_the_service_nothing_is_polled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        DotNetSigningServer.Services.Backoffice.BackofficeRegistration.AddRuntimeSettings(services, new DotNetSigningServer.Options.P4BackofficeProductOptions
        {
            BaseUrl = OutboxTestHost.BaseUrl,
            SecretKey = OutboxTestHost.SecretKey,
            DisabledReason = DotNetSigningServer.Services.Backoffice.BackofficeDisabledReason.PrivateServer,
        });
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<RuntimeSettingsClient>());
        Assert.False(provider.GetRequiredService<IRuntimeSettings>().AiEnabled());
        Assert.Equal(20, provider.GetRequiredService<IRuntimeSettings>().MaxPendingPresignsPerUser());
    }

    [Theory]
    [InlineData("Off", false)]
    [InlineData("off", false)]
    [InlineData("On", true)]
    [InlineData(null, true)]
    public void A_global_Off_rolls_the_settings_service_back(string? mode, bool expected)
    {
        var options = new DotNetSigningServer.Options.P4BackofficeProductOptions
        {
            Mode = mode,
            BaseUrl = OutboxTestHost.BaseUrl,
            SecretKey = OutboxTestHost.SecretKey,
        };
        Assert.Equal(expected, options.RuntimeServiceEnabled);
    }

    [Fact]
    public void Settings_changed_is_subscribed()
    {
        Assert.Contains("settings.changed", DotNetSigningServer.Services.Backoffice.Inbox.BackofficeEventTypes.Subscribed);
    }
}
