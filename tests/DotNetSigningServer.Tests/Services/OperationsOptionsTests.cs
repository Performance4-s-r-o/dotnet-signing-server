using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Tests.Services;

/// <summary>Operations options are refused at startup, not discovered after a paid job (#77).</summary>
public class OperationsOptionsTests
{
    private static IOptions<OperationsOptions> Resolve(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>($"Operations:{s.Key}", s.Value)))
            .Build();
        return new ServiceCollection().AddApiOperations(configuration).BuildServiceProvider()
            .GetRequiredService<IOptions<OperationsOptions>>();
    }

    [Fact]
    public void Defaults_are_valid()
    {
        var options = Resolve().Value;
        Assert.Equal(TimeSpan.FromMinutes(10), options.Lease);
    }

    [Theory]
    [InlineData("HeartbeatInterval", "00:00:00")]
    [InlineData("HeartbeatInterval", "00:05:00")] // more than a third of the 10 min lease
    [InlineData("MaxStoredResultBytes", "-1")]
    [InlineData("ResultRetention", "00:00:00")]
    public void Invalid_values_are_refused(string key, string value)
    {
        Assert.Throws<OptionsValidationException>(() => Resolve((key, value)).Value);
    }

    [Fact]
    public async Task A_broken_heartbeat_does_not_throw_when_the_work_is_done()
    {
        var heartbeat = new LeaseHeartbeat(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Guid.NewGuid(), attempt: 1, interval: TimeSpan.Zero, NullLogger.Instance);

        await heartbeat.DisposeAsync();
    }
}
