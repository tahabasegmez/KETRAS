
using Ketras.Map.Rendering;

using MColor = Mapsui.Styles.Color;

namespace Ketras.Map.Telemetry;

/// <summary>Registry: every vehicle the map knows, with a telemetry queue, animated tracks and per-traffic colours.</summary>
internal sealed class VehicleTrackStore
{
    /// <summary>Reserved id the ownship is always stored under, whatever the link calls it.</summary>
    public const string OwnshipId = "OWNSHIP";

    private const int MaxTrailPoints = 5000;
    private const double MinTrailSegmentMapUnits = 3d;

    private const double MinFrameSeconds = 1d / 60d;
    private const double MaxFrameSeconds = 1d / 5d;
    private static readonly TimeSpan DefaultFrameInterval = TimeSpan.FromSeconds(1d / 30d);

    private readonly object _queueLock = new();
    private readonly Dictionary<string, MapAircraft> _pending = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, VehicleTrack> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MColor> _trafficColors = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<VehicleTrack> Tracks => _tracks.Values;

    public VehicleTrack? Ownship => _tracks.GetValueOrDefault(OwnshipId);

    /// <summary>Thread-safe. Returns false when the sample carries no usable identity.</summary>
    public bool Enqueue(MapAircraft telemetry)
    {
        if (string.IsNullOrWhiteSpace(telemetry.Id) && !telemetry.IsOwnship)
        {
            return false;
        }

        lock (_queueLock)
        {
            _pending[IdOf(telemetry)] = telemetry;
        }

        return true;
    }

    /// <summary>Takes everything queued since the last drain. Empty when nothing arrived.</summary>
    public IReadOnlyList<MapAircraft> DrainPending()
    {
        lock (_queueLock)
        {
            if (_pending.Count == 0)
            {
                return [];
            }

            var batch = _pending.Values.ToList();
            _pending.Clear();
            return batch;
        }
    }

    /// <summary>Folds one sample into its track, creating the track on first sight.</summary>
    public VehicleTrack Apply(MapAircraft telemetry)
    {
        var vehicleId = IdOf(telemetry);

        if (!_tracks.TryGetValue(vehicleId, out var track))
        {
            track = new VehicleTrack(vehicleId, telemetry.IsOwnship);
            _tracks[vehicleId] = track;
        }

        track.Apply(telemetry, MaxTrailPoints, MinTrailSegmentMapUnits);
        return track;
    }

    /// <summary>Advances all animations by <paramref name="dtSeconds"/> of real time. False when nothing moved.</summary>
    public bool Advance(double dtSeconds)
    {
        var changed = false;

        foreach (var track in _tracks.Values)
        {
            changed |= track.Advance(dtSeconds);
        }

        return changed;
    }

    /// <summary>
    /// Frame-timer interval to poll at: twice the fastest vehicle's observed telemetry rate,
    /// clamped to [5 Hz, 60 Hz] so an idle feed does not spin the timer and a fast one is not
    /// starved of frames.
    /// </summary>
    public TimeSpan RecommendedFrameInterval()
    {
        if (_tracks.Count == 0)
        {
            return DefaultFrameInterval;
        }

        var fastestInterval = _tracks.Values.Min(track => track.UpdateIntervalSeconds);
        var seconds = Math.Clamp(fastestInterval / 2d, MinFrameSeconds, MaxFrameSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Colour for a traffic aircraft: picked from the palette on first sight and stable from then on.
    /// </summary>
    public MColor TrafficColor(VehicleTrack track)
    {
        if (_trafficColors.TryGetValue(track.VehicleId, out var existing))
        {
            return existing;
        }

        var picked = MapPalette.TrafficColor(_trafficColors.Count);
        _trafficColors[track.VehicleId] = picked;
        return picked;
    }

    /// <summary>Drops every track and anything queued.</summary>
    public void Reset()
    {
        _tracks.Clear();
        _trafficColors.Clear();

        lock (_queueLock)
        {
            _pending.Clear();
        }
    }

    private static string IdOf(MapAircraft telemetry) =>
        telemetry.IsOwnship ? OwnshipId : telemetry.Id;
}
