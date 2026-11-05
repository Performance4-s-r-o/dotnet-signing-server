using System.Globalization;
using System.Text.Json;
using DotNetSigningServer.Options;

namespace DotNetSigningServer.Services.Settings;

/// <summary>
/// Runtime settings read on use (per request), never copied into constants.
///
/// A <see cref="RuntimeSettingFailMode.LastKnown"/> key resolves as: env override, then the value
/// from the settings service (which is also the last known one), then the default in code.
/// A <see cref="RuntimeSettingFailMode.Closed"/> key is on only when the service said so within
/// <c>closed_max_stale_seconds</c>; env can only close it.
/// </summary>
public interface IRuntimeSettings
{
    /// <summary>Value of an int or duration key (last-known keys only).</summary>
    int GetInt(string key);

    /// <summary>Whether a closed bool key (a kill switch) is on now.</summary>
    bool IsOn(string key);
}

/// <summary>Typed accessors of the settings this product reads.</summary>
public static class RuntimeSettingsExtensions
{
    public static int MaxPendingPresignsPerUser(this IRuntimeSettings s) => s.GetInt(RuntimeSettingKeys.MaxPendingPresignsPerUser);

    public static int MaxTemplatesPerUser(this IRuntimeSettings s) => s.GetInt(RuntimeSettingKeys.MaxTemplatesPerUser);

    public static int MaxApiTokensPerUser(this IRuntimeSettings s) => s.GetInt(RuntimeSettingKeys.MaxApiTokensPerUser);

    /// <summary>The step-up window; 0 or less falls back to the default (a zero window would lock everyone out).</summary>
    public static TimeSpan ReauthWindow(this IRuntimeSettings s) =>
        PositiveMinutes(s.GetInt(RuntimeSettingKeys.AuthReauthMinutes), RuntimeSettingKeys.AuthReauthMinutes);

    public static int OtpMaxFailedAttempts(this IRuntimeSettings s) => Math.Max(1, s.GetInt(RuntimeSettingKeys.AuthOtpMaxFailedAttempts));

    public static int AccountMaxAttempts(this IRuntimeSettings s) => s.GetInt(RuntimeSettingKeys.AuthAccountMaxAttempts);

    public static TimeSpan AccountWindow(this IRuntimeSettings s) =>
        PositiveMinutes(s.GetInt(RuntimeSettingKeys.AuthAccountWindowMinutes), RuntimeSettingKeys.AuthAccountWindowMinutes);

    /// <summary>The AI kill switch (closed): off without a fresh "on" from the settings service.</summary>
    public static bool AiEnabled(this IRuntimeSettings s) => s.IsOn(RuntimeSettingKeys.AiEnabled);

    private static TimeSpan PositiveMinutes(int minutes, string key) =>
        TimeSpan.FromMinutes(minutes > 0 ? minutes : RuntimeSettingKeys.Get(key).Default);
}

/// <summary>One item of <c>GET /v1/settings</c>, as far as this product reads it.</summary>
public sealed record ServiceSetting(string Key, JsonElement Value, bool Closed);

/// <summary>A successful <c>GET /v1/settings</c> body.</summary>
public sealed record RuntimeSettingsSnapshot(
    long Version,
    int PollIntervalSeconds,
    int ClosedMaxStaleSeconds,
    IReadOnlyDictionary<string, ServiceSetting> Items)
{
    public const int DefaultPollIntervalSeconds = 30;
    public const int DefaultClosedMaxStaleSeconds = 300;

    /// <summary>Parses a response body; throws <see cref="JsonException"/> when it is not one.</summary>
    public static RuntimeSettingsSnapshot Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out var v) || !v.TryGetInt64(out var version)
            || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Settings response has no version or items");
        }

        var byKey = new Dictionary<string, ServiceSetting>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String) continue;
            var key = k.GetString()!;
            // value ?? default, as the service defines it.
            var value = item.TryGetProperty("value", out var raw) && raw.ValueKind != JsonValueKind.Null
                ? raw
                : item.TryGetProperty("default", out var d) ? d : default;
            var closed = item.TryGetProperty("fail_mode", out var f) && f.ValueKind == JsonValueKind.String && f.GetString() == "closed";
            byKey[key] = new ServiceSetting(key, value.ValueKind == JsonValueKind.Undefined ? default : value.Clone(), closed);
        }

        return new RuntimeSettingsSnapshot(
            version,
            PositiveInt(root, "poll_interval_seconds") ?? DefaultPollIntervalSeconds,
            PositiveInt(root, "closed_max_stale_seconds") ?? DefaultClosedMaxStaleSeconds,
            byKey);
    }

    private static int? PositiveInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) && n > 0 ? n : null;
}

/// <summary>
/// Env overrides of the runtime settings: the key in upper case (<c>MAX_PENDING_PRESIGNS_PER_USER</c>).
/// Read once at start. An int that does not parse, or is negative, stops the start (an unreadable
/// value never quietly becomes the default). A closed key can only be closed: <c>false</c> applies,
/// anything else is ignored with a warning.
/// </summary>
public sealed class RuntimeSettingsOverrides
{
    private readonly Dictionary<string, int> _ints = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closed = new(StringComparer.Ordinal);

    /// <summary>Overrides that were ignored, for one warning each at start.</summary>
    public IReadOnlyList<string> Ignored { get; }

    public static readonly RuntimeSettingsOverrides None = new([]);

    private RuntimeSettingsOverrides(List<string> ignored) => Ignored = ignored;

    public static RuntimeSettingsOverrides From(IConfiguration configuration)
    {
        var ignored = new List<string>();
        var result = new RuntimeSettingsOverrides(ignored);
        foreach (var definition in RuntimeSettingKeys.All)
        {
            var raw = configuration[definition.EnvName];
            if (EnvValues.IsUnset(raw)) continue;
            var text = raw!.Trim();
            if (definition.FailMode == RuntimeSettingFailMode.Closed)
            {
                if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) result._closed.Add(definition.Key);
                else ignored.Add($"{definition.EnvName} can only close the switch (false); \"{text}\" is ignored");
                continue;
            }
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
            {
                throw new InvalidOperationException($"{definition.EnvName} must be 0 or a positive whole number, got '{text}'.");
            }
            result._ints[definition.Key] = value;
        }
        return result;
    }

    public bool TryGetInt(string key, out int value) => _ints.TryGetValue(key, out value);

    public bool Closes(string key) => _closed.Contains(key);

    /// <summary>Overrides from a dictionary of env names (tests).</summary>
    public static RuntimeSettingsOverrides FromValues(IReadOnlyDictionary<string, string?> values) =>
        From(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}

/// <summary>
/// The settings in memory (singleton). <see cref="RuntimeSettingsClient"/> feeds it; request paths
/// only read it. Without a client (private server, no service configured) every last-known key is
/// its env override or its default and every closed key is closed.
/// </summary>
public sealed class RuntimeSettings : IRuntimeSettings
{
    private sealed record State(RuntimeSettingsSnapshot? Snapshot, DateTimeOffset? LastSuccessAt);

    private readonly RuntimeSettingsOverrides _overrides;
    private readonly TimeProvider _time;
    private readonly ILogger<RuntimeSettings> _logger;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private volatile State _state = new(null, null);

    public RuntimeSettings(RuntimeSettingsOverrides overrides, TimeProvider time, ILogger<RuntimeSettings> logger)
    {
        _overrides = overrides;
        _time = time;
        _logger = logger;
    }

    /// <summary>Version of the snapshot in memory; null before the first successful read.</summary>
    public long? Version => _state.Snapshot?.Version;

    public RuntimeSettingsSnapshot? Snapshot => _state.Snapshot;

    public DateTimeOffset? LastSuccessAt => _state.LastSuccessAt;

    /// <summary>
    /// A successful read: a newer (or equal) snapshot replaces the one in memory; an older one is
    /// not taken, but the read still counts as contact with the service.
    /// </summary>
    public void Apply(RuntimeSettingsSnapshot snapshot, DateTimeOffset at)
    {
        var current = _state;
        if (current.Snapshot is { } known && snapshot.Version < known.Version)
        {
            _logger.LogWarning("[settings] the service answered version {Version}, older than the known {Known}; kept the known one",
                snapshot.Version, known.Version);
            _state = current with { LastSuccessAt = at };
            return;
        }
        _state = new State(snapshot, at);
    }

    /// <summary>A <c>304 Not Modified</c>: nothing changed, but the closed keys are fresh again.</summary>
    public void MarkNotModified(DateTimeOffset at) => _state = _state with { LastSuccessAt = at };

    public int GetInt(string key)
    {
        var definition = RuntimeSettingKeys.Get(key);
        if (definition.FailMode != RuntimeSettingFailMode.LastKnown)
        {
            throw new InvalidOperationException($"{key} is a closed switch; read it with IsOn");
        }
        if (_overrides.TryGetInt(key, out var overridden)) return overridden;

        var item = _state.Snapshot?.Items.GetValueOrDefault(key);
        if (item is not null)
        {
            if (item.Value.ValueKind == JsonValueKind.Number && item.Value.TryGetInt32(out var value) && value >= 0) return value;
            if (FirstWarning(key))
            {
                _logger.LogWarning("[settings] {Key} from the service is not a whole number; the default in code applies", key);
            }
        }
        return definition.Default;
    }

    public bool IsOn(string key)
    {
        var definition = RuntimeSettingKeys.Get(key);
        if (definition.FailMode != RuntimeSettingFailMode.Closed || definition.Type != RuntimeSettingType.Bool)
        {
            throw new InvalidOperationException($"{key} is not a closed switch");
        }
        if (_overrides.Closes(key)) return false;

        var state = _state;
        if (state.Snapshot is not { } snapshot || state.LastSuccessAt is not { } at) return false;
        if (_time.GetUtcNow() - at > TimeSpan.FromSeconds(snapshot.ClosedMaxStaleSeconds)) return false;
        var item = snapshot.Items.GetValueOrDefault(key);
        return item is not null && item.Value.ValueKind == JsonValueKind.True;
    }

    private bool FirstWarning(string key)
    {
        lock (_warned) return _warned.Add(key);
    }
}
