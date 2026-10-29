
using Ketras.Map.Rendering;
using Mapsui;

namespace Ketras.Map.Telemetry;

/// <summary>
/// One vehicle on the map, with a smoothed position eased toward the latest reported telemetry.
/// Stale once its data age exceeds three observed update intervals; intervals over 2 s are not
/// learned, so feeds slower than 0.5 Hz can flicker stale between samples.
/// </summary>
internal sealed class VehicleTrack
{
    private const double DefaultUpdateIntervalSeconds = 1d / 10d;
    private const double MinUpdateIntervalSeconds = 1d / 60d;
    private const double MaxUpdateIntervalSeconds = 2d;
    private const double IntervalSmoothing = 0.3d;
    private const double StaleIntervals = 3d;
    private const double MinStaleSeconds = 1.5d;

    private readonly List<MPoint> _trail = [];
    private DateTime _lastSampleAt;
    private TimeSpan _ageAtArrival;

    internal VehicleTrack(string vehicleId, bool isOwnship)
    {
        VehicleId = vehicleId;
        IsOwnship = isOwnship;
        DisplayName = vehicleId;
    }

    public string VehicleId { get; }

    public bool IsOwnship { get; }

    /// <summary>False until the first telemetry sample lands; nothing is drawn before that.</summary>
    public bool HasFirstFix { get; private set; }

    public string DisplayName { get; private set; }

    public MPoint CurrentWorld { get; private set; } = new();

    public MPoint TargetWorld { get; private set; } = new();

    public double CurrentHeading { get; private set; }

    public double TargetHeading { get; private set; }

    public double AltitudeMeters { get; private set; }

    public double GroundSpeedMetersPerSecond { get; private set; }

    public string Mode { get; private set; } = "UNKNOWN";

    public bool IsStale { get; private set; }

    public IReadOnlyList<MPoint> Trail => _trail;

    /// <summary>Observed time between telemetry samples, tracked so easing matches the feed's own rate instead of a fixed constant.</summary>
    internal double UpdateIntervalSeconds { get; private set; } = DefaultUpdateIntervalSeconds;

    internal void Apply(MapAircraft telemetry, int maxTrailPoints, double minTrailSegmentMapUnits)
    {
        var target = MapGeometry.ToMercator(telemetry.Position);
        var now = DateTime.UtcNow;

        // First fix is placed, not eased: easing from (0,0) would fly across the world.
        if (!HasFirstFix)
        {
            CurrentWorld = target;
            CurrentHeading = telemetry.HeadingDegrees;
            HasFirstFix = true;
        }
        else
        {
            var interval = (now - _lastSampleAt).TotalSeconds;

            if (interval is >= MinUpdateIntervalSeconds and <= MaxUpdateIntervalSeconds)
            {
                UpdateIntervalSeconds = MapGeometry.Lerp(UpdateIntervalSeconds, interval, IntervalSmoothing);
            }
        }

        _lastSampleAt = now;
        _ageAtArrival = telemetry.SampleAge ?? TimeSpan.Zero;
        TargetWorld = target;
        TargetHeading = telemetry.HeadingDegrees;
        AltitudeMeters = telemetry.AltitudeMeters;
        GroundSpeedMetersPerSecond = telemetry.GroundSpeedMps;
        Mode = telemetry.Mode;
        DisplayName = string.IsNullOrWhiteSpace(telemetry.Name) ? VehicleId : telemetry.Name;

        AppendTrailPoint(target, maxTrailPoints, minTrailSegmentMapUnits);
    }

    /// <summary>
    /// Eases one step toward the reported pose. The smoothing factor is derived from
    /// <paramref name="dtSeconds"/> and the feed's own <see cref="UpdateIntervalSeconds"/>
    /// (exponential decay, time constant = half the update interval), so motion stays smooth
    /// whether the feed reports at 1 Hz or 50 Hz. True when worth a repaint, including when the
    /// track turns stale or fresh, so a dead feed still repaints.
    /// </summary>
    internal bool Advance(double dtSeconds)
    {
        if (!HasFirstFix)
        {
            return false;
        }

        var age = _ageAtArrival + (DateTime.UtcNow - _lastSampleAt);
        var wasStale = IsStale;
        IsStale = age.TotalSeconds > Math.Max(StaleIntervals * UpdateIntervalSeconds, MinStaleSeconds);

        var timeConstant = Math.Max(UpdateIntervalSeconds * 0.5, MinUpdateIntervalSeconds);
        var alpha = Math.Clamp(1d - Math.Exp(-dtSeconds / timeConstant), 0d, 1d);

        var nextX = MapGeometry.Lerp(CurrentWorld.X, TargetWorld.X, alpha);
        var nextY = MapGeometry.Lerp(CurrentWorld.Y, TargetWorld.Y, alpha);
        var nextHeading = MapGeometry.LerpAngle(CurrentHeading, TargetHeading, alpha);

        var moved = Math.Abs(nextX - CurrentWorld.X) > 0.001 ||
                    Math.Abs(nextY - CurrentWorld.Y) > 0.001 ||
                    Math.Abs(nextHeading - CurrentHeading) > 0.05;

        CurrentWorld = new MPoint(nextX, nextY);
        CurrentHeading = nextHeading;
        return moved || wasStale != IsStale;
    }

    private void AppendTrailPoint(MPoint point, int maxTrailPoints, double minSegmentMapUnits)
    {
        if (_trail.Count == 0)
        {
            _trail.Add(point);
            return;
        }

        // Skip jitter so a parked vehicle cannot fill the buffer.
        if (MapGeometry.DistanceBetween(_trail[^1], point) < minSegmentMapUnits)
        {
            return;
        }

        _trail.Add(point);

        if (_trail.Count > maxTrailPoints)
        {
            _trail.RemoveRange(0, _trail.Count - maxTrailPoints);
        }
    }
}
