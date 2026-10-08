using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ketras.Station.Settings;
using Microsoft.Extensions.Logging;

namespace Ketras.Station.Links;

/// <summary>
/// The operator's saved link profiles and the one used last, in <c>links.json</c> in the configuration directory. It only
/// remembers; nothing connects from here. Thread-safe.
/// </summary>
public sealed class LinkProfileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;
    private readonly ILogger<LinkProfileStore> _logger;
    private readonly object _lock = new();
    private List<LinkProfile> _profiles = [];
    private string? _lastUsed;

    /// <summary>Opens the store; a missing file is an empty store.</summary>
    public LinkProfileStore(StationPaths paths, ILogger<LinkProfileStore> logger)
    {
        _path = paths.ConfigFile("links.json");
        _logger = logger;
        Load();
    }

    /// <summary>The saved profiles changed.</summary>
    public event Action? Changed;

    /// <summary>Saved profiles in the order they were added.</summary>
    public IReadOnlyList<LinkProfile> Profiles
    {
        get
        {
            lock (_lock)
            {
                return [.. _profiles];
            }
        }
    }

    /// <summary>Name of the profile connected last, if it is still saved.</summary>
    public string? LastUsed
    {
        get
        {
            lock (_lock)
            {
                return _profiles.Any(p => p.Name == _lastUsed) ? _lastUsed : null;
            }
        }
    }

    /// <summary>Adds a profile or replaces the one with its name; <paramref name="replaces"/> renames an existing one.</summary>
    public void Save(LinkProfile profile, string? replaces = null)
    {
        lock (_lock)
        {
            if (replaces is not null && replaces != profile.Name)
            {
                _profiles.RemoveAll(p => p.Name == replaces);
            }

            var index = _profiles.FindIndex(p => p.Name == profile.Name);
            if (index >= 0)
            {
                _profiles[index] = profile;
            }
            else
            {
                _profiles.Add(profile);
            }

            Write();
        }

        Changed?.Invoke();
    }

    /// <summary>Forgets a profile.</summary>
    public void Remove(string name)
    {
        lock (_lock)
        {
            if (_profiles.RemoveAll(p => p.Name == name) == 0)
            {
                return;
            }

            Write();
        }

        Changed?.Invoke();
    }

    /// <summary>Remembers the profile connected last.</summary>
    public void MarkUsed(string name)
    {
        lock (_lock)
        {
            _lastUsed = name;
            Write();
        }
    }

    private sealed class Document
    {
        public List<LinkProfile> Profiles { get; set; } = [];

        public string? LastUsed { get; set; }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            // A profile of a kind this version does not know is dropped, not the whole file.
            var document = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
            if (document?["profiles"] is JsonArray profiles)
            {
                foreach (var unknown in profiles.Where(p => p?["kind"] is not JsonValue kind || !Enum.TryParse<LinkKind>(kind.ToString(), true, out _)).ToList())
                {
                    profiles.Remove(unknown);
                }
            }

            var parsed = document?.Deserialize<Document>(Json);
            _profiles = parsed?.Profiles ?? [];
            _lastUsed = parsed?.LastUsed;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Saved links in {Path} could not be read", _path);
            _profiles = [];
        }
    }

    private void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Document { Profiles = _profiles, LastUsed = _lastUsed }, Json));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "Saved links could not be written to {Path}", _path);
        }
    }
}
