using DotNetSigningServer.Services;

namespace DotNetSigningServer.Logging;

/// <summary>
/// Forwards ILogger output to Loki via <see cref="LokiClient"/>. Registered as
/// an <see cref="ILoggerProvider"/> so every <c>Logger.LogInformation/...</c>
/// call across the app — not just unhandled exceptions — reaches Loki.
///
/// <see cref="LokiClient"/> is resolved lazily from the root service provider:
/// at logging-builder time the container isn't built yet, and resolving on the
/// first log call is safe because <see cref="LokiClient"/> only depends on
/// singletons (IHttpClientFactory, IOptions).
/// </summary>
public sealed class LokiLoggerProvider : ILoggerProvider
{
    private readonly IServiceProvider _serviceProvider;
    private LokiClient? _client;

    public LokiLoggerProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public ILogger CreateLogger(string categoryName)
        => new LokiLogger(categoryName, () => _client ??= _serviceProvider.GetRequiredService<LokiClient>());

    public void Dispose()
    {
    }
}

internal sealed class LokiLogger : ILogger
{
    private readonly string _category;
    private readonly Func<LokiClient> _clientAccessor;

    public LokiLogger(string category, Func<LokiClient> clientAccessor)
    {
        _category = category;
        _clientAccessor = clientAccessor;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel)
    {
        // Trace/Debug never go to Loki — info and above only.
        if (logLevel < LogLevel.Information)
        {
            return false;
        }

        // Feedback-loop guard: LokiClient ships logs over HttpClient, and the
        // HTTP / logging-infrastructure categories would otherwise generate
        // more log entries for every push.
        if (_category.StartsWith("System.Net.Http", StringComparison.Ordinal)
            || _category.StartsWith("Microsoft.Extensions.Http", StringComparison.Ordinal)
            || _category.Contains("LokiClient", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (exception != null)
        {
            message += "\n" + exception;
        }

        var level = logLevel switch
        {
            LogLevel.Critical => "error",
            LogLevel.Error => "error",
            LogLevel.Warning => "warn",
            _ => "info",
        };

        var labels = new Dictionary<string, string> { ["category"] = _category };
        if (eventId.Id != 0)
        {
            labels["eventId"] = eventId.Id.ToString();
        }

        try
        {
            _clientAccessor().Enqueue(level, message, labels);
        }
        catch
        {
            // Logging must never throw — e.g. a framework log emitted before
            // the DI container finished building can't resolve LokiClient yet.
        }
    }
}
