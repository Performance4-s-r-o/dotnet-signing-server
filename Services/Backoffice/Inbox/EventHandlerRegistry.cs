namespace DotNetSigningServer.Services.Backoffice.Inbox;

/// <summary>
/// Maps event types to handlers. Every registered <see cref="IBackofficeEventHandler"/>
/// takes precedence over <see cref="LoggingBackofficeEventHandler"/>, which covers whatever
/// is subscribed but not handled yet. Two real handlers for one type is a wiring mistake and
/// fails on first use.
/// </summary>
public sealed class EventHandlerRegistry
{
    private readonly Dictionary<string, IBackofficeEventHandler> _handlers = new(StringComparer.Ordinal);

    public EventHandlerRegistry(IEnumerable<IBackofficeEventHandler> handlers, LoggingBackofficeEventHandler fallback)
    {
        foreach (var handler in handlers)
        {
            foreach (var type in handler.Types)
            {
                if (_handlers.TryGetValue(type, out var existing) && !ReferenceEquals(existing, handler))
                {
                    throw new InvalidOperationException(
                        $"Backoffice event type {type} has two handlers: {existing.GetType().Name} and {handler.GetType().Name}.");
                }
                _handlers[type] = handler;
            }
        }

        foreach (var type in fallback.Types)
        {
            _handlers.TryAdd(type, fallback);
        }
    }

    /// <summary>The handler of <paramref name="type"/>; null for a type nothing handles.</summary>
    public IBackofficeEventHandler? Find(string type) => _handlers.GetValueOrDefault(type);

    public IReadOnlyCollection<string> Types => _handlers.Keys;
}
