using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Ketras.Events;
using Ketras.Mavlink.Protocols;
using Ketras.Model.Vehicles;
using Ketras.Settings;
using Ketras.Vehicles;
using Microsoft.Extensions.Logging;

namespace Ketras.Parameters;

/// <summary>Where a vehicle's metadata came from.</summary>
public enum MetadataSource
{
    /// <summary>Nowhere: the vehicle provides none and no download worked.</summary>
    None,

    /// <summary>From the vehicle over MAVLink FTP (or its cached copy).</summary>
    Vehicle,

    /// <summary>Downloaded from the address the vehicle names (or its cached copy).</summary>
    Download
}

/// <summary>A vehicle's metadata: parameter descriptions by name, the actuator description and the event definitions.</summary>
/// <param name="Parameters">Parameter metadata; empty when there is none.</param>
/// <param name="ActuatorsJson">PX4 actuator metadata (type 5); null when there is none.</param>
/// <param name="Source">Where the parameter metadata came from.</param>
/// <param name="Notes">What did not work, for the log.</param>
public sealed record VehicleMetadata(IReadOnlyDictionary<string, ParameterMetadata> Parameters, string? ActuatorsJson, MetadataSource Source, IReadOnlyList<string> Notes)
{
    /// <summary>No metadata.</summary>
    public static VehicleMetadata Empty { get; } = new(new Dictionary<string, ParameterMetadata>(), null, MetadataSource.None, []);

    /// <summary>The metadata of <paramref name="name"/>, if any.</summary>
    public ParameterMetadata? this[string name] => Parameters.GetValueOrDefault(name);

    /// <summary>The events metadata (type 4); null when there is none.</summary>
    public EventDefinitions? Events { get; init; }
}

/// <summary>
/// Loads each vehicle's metadata once its parameters are read: from the vehicle over MAVLink FTP, else from the
/// internet address it names (<c>uriFallback</c>), cached on disk by CRC so a known firmware loads offline; nothing is
/// bundled. Loads for one vehicle share one task.
/// </summary>
public sealed class MetadataService : IDisposable
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);

    private readonly VehicleRegistry _vehicles;
    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly ILogger<MetadataService> _logger;
    private readonly ConcurrentDictionary<VehicleId, Task<VehicleMetadata>> _loads = new();
    private readonly ConcurrentDictionary<VehicleId, VehicleMetadata> _loaded = new();

    /// <summary>Loads metadata for every vehicle the registry adds.</summary>
    public MetadataService(VehicleRegistry vehicles, StationPaths paths, ILogger<MetadataService> logger)
        : this(vehicles, paths, logger, new SocketsHttpHandler())
    {
    }

    internal MetadataService(VehicleRegistry vehicles, StationPaths paths, ILogger<MetadataService> logger, HttpMessageHandler handler)
    {
        _vehicles = vehicles;
        _logger = logger;
        _cacheDirectory = paths.DataPath(null, "metadata");
        _http = new HttpClient(handler) { Timeout = DownloadTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Ketras");
        vehicles.VehicleAdded += OnVehicleAdded;
        vehicles.VehicleRemoved += OnVehicleRemoved;
        foreach (var vehicle in vehicles.Vehicles)
        {
            OnVehicleAdded(vehicle);
        }
    }

    /// <summary>A vehicle's metadata finished loading (possibly empty). Raised on a worker thread.</summary>
    public event Action<VehicleId, VehicleMetadata>? Loaded;

    /// <summary>The loaded metadata of <paramref name="id"/>; null while it loads or before.</summary>
    public VehicleMetadata? For(VehicleId id) => _loaded.GetValueOrDefault(id);

    /// <summary>The metadata is being loaded for <paramref name="id"/>.</summary>
    public bool IsLoading(VehicleId id) => _loads.TryGetValue(id, out var load) && !load.IsCompleted;

    /// <summary>Loads (once) the metadata of <paramref name="vehicle"/>.</summary>
    public Task<VehicleMetadata> LoadAsync(Vehicle vehicle) => _loads.GetOrAdd(vehicle.Id, _ => Task.Run(() => LoadCoreAsync(vehicle)));

    /// <inheritdoc />
    public void Dispose()
    {
        _vehicles.VehicleAdded -= OnVehicleAdded;
        _vehicles.VehicleRemoved -= OnVehicleRemoved;
        _http.Dispose();
    }

    private void OnVehicleAdded(Vehicle vehicle)
    {
        if (vehicle.Parameters.IsLoaded)
        {
            _ = LoadAsync(vehicle);
            return;
        }

        void OnLoaded(Mavlink.Protocols.ParameterListResult result)
        {
            vehicle.Parameters.Loaded -= OnLoaded;
            _ = LoadAsync(vehicle);
        }

        vehicle.Parameters.Loaded += OnLoaded;
    }

    private void OnVehicleRemoved(Vehicle vehicle)
    {
        _loads.TryRemove(vehicle.Id, out _);
        _loaded.TryRemove(vehicle.Id, out _);
    }

    private async Task<VehicleMetadata> LoadCoreAsync(Vehicle vehicle)
    {
        var notes = new List<string>();
        ComponentMetadata files;
        try
        {
            files = await vehicle.Session.Metadata.LoadAsync(_cacheDirectory);
            notes.AddRange(files.Notes);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            notes.Add($"metadata from the vehicle: {exception.Message}");
            files = new ComponentMetadata(null, null, []);
        }

        var source = files.ParametersJson is null ? MetadataSource.None : MetadataSource.Vehicle;
        var parametersJson = files.ParametersJson;
        if (parametersJson is null && files.ParametersFallback is { } parametersFallback)
        {
            parametersJson = await DownloadAsync(parametersFallback, "parameters", notes);
            source = parametersJson is null ? MetadataSource.None : MetadataSource.Download;
        }

        var actuatorsJson = files.ActuatorsJson;
        if (actuatorsJson is null && files.ActuatorsFallback is { } actuatorsFallback)
        {
            actuatorsJson = await DownloadAsync(actuatorsFallback, "actuators", notes);
        }

        var eventsJson = files.EventsJson;
        if (eventsJson is null && files.EventsFallback is { } eventsFallback)
        {
            eventsJson = await DownloadAsync(eventsFallback, "events", notes);
        }

        EventDefinitions? events = null;
        if (eventsJson is not null)
        {
            try
            {
                events = EventsParser.Parse(eventsJson);
            }
            catch (FormatException exception)
            {
                notes.Add(exception.Message);
            }
        }

        IReadOnlyDictionary<string, ParameterMetadata> parameters = new Dictionary<string, ParameterMetadata>();
        if (parametersJson is not null)
        {
            try
            {
                parameters = ParameterMetadataParser.Parse(parametersJson);
            }
            catch (FormatException exception)
            {
                notes.Add($"parameter metadata: {exception.Message}");
                source = MetadataSource.None;
            }
        }

        var metadata = new VehicleMetadata(parameters, actuatorsJson, source, notes) { Events = events };
        _logger.LogInformation("Metadata of {Vehicle}: {Count} parameters from {Source}, actuators {Actuators}, {Events} events{Notes}", vehicle.Id, parameters.Count, source,
            actuatorsJson is null ? "none" : "loaded", events?.Count ?? 0, notes.Count == 0 ? string.Empty : "; " + string.Join("; ", notes));
        _loaded[vehicle.Id] = metadata;
        Loaded?.Invoke(vehicle.Id, metadata);
        return metadata;
    }

    private async Task<string?> DownloadAsync(MetadataFallback fallback, string kind, List<string> notes)
    {
        var cacheFile = Path.Combine(_cacheDirectory, fallback.Crc != 0
            ? $"{kind}-{fallback.Crc:x8}.json"
            : $"{kind}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fallback.Uri)))[..16].ToLowerInvariant()}.json");
        if (fallback.Crc != 0 && File.Exists(cacheFile))
        {
            return await File.ReadAllTextAsync(cacheFile);
        }

        try
        {
            var bytes = await _http.GetByteArrayAsync(fallback.Uri);
            var text = ComponentMetadataClient.DecodeFile(fallback.Uri, bytes);
            Directory.CreateDirectory(_cacheDirectory);
            await File.WriteAllTextAsync(cacheFile, text);
            return text;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            notes.Add($"{fallback.Uri}: {exception.Message}");
            return File.Exists(cacheFile) ? await File.ReadAllTextAsync(cacheFile) : null;
        }
    }
}
