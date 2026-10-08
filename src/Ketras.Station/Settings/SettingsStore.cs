using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Ketras.Station.Settings;

/// <summary>A user setting: its key, default and, for a choice, the allowed values.</summary>
/// <typeparam name="T">Value type (a JSON-serializable primitive or enum).</typeparam>
/// <param name="Key">Stable key, e.g. "instruments.style".</param>
/// <param name="Default">Value when nothing is stored.</param>
public sealed record SettingDefinition<T>(string Key, T Default);

/// <summary>
/// The operator's own settings (display style, units, last choices), stored as JSON in the configuration directory and
/// read on start. Deployment settings live in <see cref="StationOptions"/> instead. Thread-safe.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<SettingsStore> _logger;
    private readonly object _lock = new();
    private readonly JsonObject _values;

    /// <summary>Opens the store at <c>settings.json</c> in the configuration directory.</summary>
    public SettingsStore(StationPaths paths, ILogger<SettingsStore> logger)
    {
        _path = paths.ConfigFile("settings.json");
        _logger = logger;
        _values = Load();
    }

    /// <summary>A setting changed: its key.</summary>
    public event Action<string>? Changed;

    /// <summary>The stored value, or the default when none is stored or it cannot be read.</summary>
    public T Get<T>(SettingDefinition<T> setting)
    {
        lock (_lock)
        {
            try
            {
                return _values[setting.Key] is { } node && node.Deserialize<T>() is { } value ? value : setting.Default;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
            {
                return setting.Default;
            }
        }
    }

    /// <summary>Stores a value and saves the file.</summary>
    public void Set<T>(SettingDefinition<T> setting, T value)
    {
        lock (_lock)
        {
            _values[setting.Key] = JsonSerializer.SerializeToNode(value);
            Save();
        }

        Changed?.Invoke(setting.Key);
    }

    private JsonObject Load()
    {
        try
        {
            return File.Exists(_path) ? JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? [] : [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Settings in {Path} could not be read; defaults apply", _path);
            return [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, _values.ToJsonString(Json));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "Settings could not be saved to {Path}", _path);
        }
    }
}
