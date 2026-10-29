using Ketras.Notifications;
using Ketras.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ketras.Environment;

/// <summary>
/// Zones and points of interest from GeoJSON or KML files: those in the settings at start, and any the operator opens
/// later. Each file is its own source, named after the file. Thread-safe.
/// </summary>
public sealed class ZoneFileEnvironmentProvider(IOptions<StationOptions> options, NotificationStore notifications, ILogger<ZoneFileEnvironmentProvider> logger)
    : IEnvironmentProvider
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private IEnvironmentSink? _sink;

    /// <inheritdoc />
    public string Name => "Zone files";

    /// <summary>The loaded files, by source name.</summary>
    public IReadOnlyList<string> Files
    {
        get
        {
            lock (_lock)
            {
                return [.. _files.Values];
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(IEnvironmentSink sink, CancellationToken cancellation)
    {
        _sink = sink;
        foreach (var path in options.Value.Environment.ZoneFiles)
        {
            Load(path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellation)
    {
        _sink = null;
        return Task.CompletedTask;
    }

    /// <summary>Loads (or reloads) a file; failures are reported as notifications. Returns whether it loaded.</summary>
    public bool Load(string path)
    {
        var source = Path.GetFileName(path);
        try
        {
            var result = ZoneFileParser.ReadFile(path, source);
            lock (_lock)
            {
                _files[source] = Path.GetFullPath(path);
            }

            _sink?.SetZones(source, result.Zones);
            _sink?.SetPoints(source, result.Points);
            logger.LogInformation("Zone file {File}: {Zones} zones, {Points} points", source, result.Zones.Count, result.Points.Count);
            if (result.Skipped.Count > 0)
            {
                notifications.Notify(Name, NotificationSeverity.Warning, $"{source}: {result.Skipped.Count} feature(s) skipped", string.Join("; ", result.Skipped));
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            logger.LogWarning(exception, "Zone file {Path} could not be read", path);
            notifications.Notify(Name, NotificationSeverity.Error, $"{source} could not be read", exception.Message);
            return false;
        }
    }

    /// <summary>Removes a file's zones and points.</summary>
    public void Unload(string source)
    {
        lock (_lock)
        {
            _files.Remove(source);
        }

        _sink?.SetZones(source, []);
        _sink?.SetPoints(source, []);
    }
}
