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
public sealed class LokiLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly IServiceProvider _serviceProvider;
    private LokiClient? _client;
    private IExternalScopeProvider? _scopes;

    /// <summary>The logging framework hands over the scopes; only the portal caller's are read (#78).</summary>
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public LokiLoggerProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public ILogger CreateLogger(string categoryName)
        => new LokiLogger(categoryName, () => _client ??= _serviceProvider.GetRequiredService<LokiClient>(), () => _scopes);

    public void Dispose()
    {
    }
}

internal sealed class LokiLogger : ILogger
{
    private readonly string _category;
    private readonly Func<LokiClient> _clientAccessor;
    private readonly Func<IExternalScopeProvider?> _scopes;

    public LokiLogger(string category, Func<LokiClient> clientAccessor, Func<IExternalScopeProvider?>? scopes = null)
    {
        _category = category;
        _clientAccessor = clientAccessor;
        _scopes = scopes ?? (() => null);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _scopes()?.Push(state);

    /// <summary>
    /// Tenant and request id of a portal call (#78), appended to the line rather than made labels:
    /// a label per tenant would be a stream per tenant.
    /// </summary>
    internal static string? PortalScopeSuffix(IExternalScopeProvider? scopes)
    {
        if (scopes == null) return null;
        string? tenant = null, request = null;
        scopes.ForEachScope((scope, _) =>
        {
            if (scope is not IEnumerable<KeyValuePair<string, object?>> pairs) return;
            foreach (var (key, value) in pairs)
            {
                if (key == DotNetSigningServer.Middleware.PortalCallerScopeMiddleware.TenantScopeKey) tenant = value?.ToString();
                else if (key == DotNetSigningServer.Middleware.PortalCallerScopeMiddleware.RequestIdScopeKey) request = value?.ToString();
            }
        }, (object?)null);
        return tenant == null && request == null ? null : $" [tenant={tenant} requestId={request}]";
    }

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

        var message = formatter(state, exception) + PortalScopeSuffix(_scopes());
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
