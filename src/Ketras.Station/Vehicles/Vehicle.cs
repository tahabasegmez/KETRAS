using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Vehicles;
using Ketras.Model.Geo;
using Ketras.Model.Vehicles;
using Ketras.Station.Events;
using Ketras.Station.Notifications;
using Microsoft.Extensions.Logging;

namespace Ketras.Station.Vehicles;

/// <summary>
/// Facade over one vehicle the station follows: its MAVLink session, an immutable <see cref="VehicleSnapshot"/>
/// published at most <see cref="SnapshotRate"/> times a second, its parameters, the planning limits derived from
/// them, its messages for the operator (STATUSTEXT and interpreted events) and its health and arming report.
/// Snapshot events are raised on a timer thread; messages and reports on the link's dispatch thread.
/// </summary>
public sealed class Vehicle : IDisposable
{
    /// <summary>Most snapshots published per second.</summary>
    public const int SnapshotRate = 10;

    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly object _lock = new();
    private VehicleSnapshot _snapshot;
    private VehicleLimits _limits = VehicleLimits.Default;
    private HealthReport? _health;
    private EventDefinitions? _events;
    private int? _battery;
    private DateTime? _armedSince;
    private int _dirty;

    internal Vehicle(VehicleSession session, string name, TimeProvider time, ILogger logger)
    {
        Session = session;
        Id = new VehicleId(session.SystemId);
        Name = name;
        _time = time;
        Parameters = new VehicleParameters(session.Parameters, logger);
        _snapshot = Build();

        var status = session.Status;
        status.PositionChanged += _ => MarkDirty();
        status.AttitudeChanged += _ => MarkDirty();
        status.StateChanged += state =>
        {
            _armedSince = state.IsArmed ? _armedSince ?? _time.GetUtcNow().UtcDateTime : null;
            MarkDirty();
        };
        status.NavigationChanged += _ => MarkDirty();
        status.EstimatorChanged += _ => MarkDirty();
        status.VibrationChanged += _ => MarkDirty();
        status.HomeChanged += _ => MarkDirty();
        status.GpsChanged += _ => MarkDirty();
        status.StatusText += OnStatusText;
        status.Battery += reading =>
        {
            _battery = reading.RemainingPercent;
            MarkDirty();
        };
        session.ConnectionChanged += _ => MarkDirty();
        Parameters.ValueChanged += entry =>
        {
            if (VehicleLimitsMapper.Px4Parameters.Contains(entry.Name))
            {
                UpdateLimits();
            }
        };
        Parameters.Loaded += _ => UpdateLimits();

        var period = TimeSpan.FromSeconds(1.0 / SnapshotRate);
        _timer = time.CreateTimer(_ => Publish(), null, period, period);
    }

    /// <summary>Which vehicle.</summary>
    public VehicleId Id { get; }

    /// <summary>Name shown to the operator.</summary>
    public string Name { get; }

    /// <summary>The MAVLink session: status, commands, parameters, missions, FTP.</summary>
    public VehicleSession Session { get; }

    /// <summary>Parameters, read once on connect.</summary>
    public VehicleParameters Parameters { get; }

    /// <summary>True while heartbeats arrive.</summary>
    public bool IsConnected => Session.IsConnected;

    /// <summary>The latest snapshot.</summary>
    public VehicleSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>The planning envelope from the vehicle's parameters; the defaults until they are known.</summary>
    public VehicleLimits Limits
    {
        get
        {
            lock (_lock)
            {
                return _limits;
            }
        }
    }

    /// <summary>The vehicle's events metadata, once loaded; its events are then interpreted.</summary>
    public EventDefinitions? EventDefinitions
    {
        get
        {
            lock (_lock)
            {
                return _events;
            }
        }

        internal set
        {
            lock (_lock)
            {
                _events = value;
            }
        }
    }

    /// <summary>The latest health and arming check report; null until the vehicle sent one (PX4 events).</summary>
    public HealthReport? Health
    {
        get
        {
            lock (_lock)
            {
                return _health;
            }
        }
    }

    /// <summary>A message for the operator: STATUSTEXT (without PX4's copies of events) and interpreted events.</summary>
    public event Action<NotificationSeverity, string>? Message;

    /// <summary><see cref="Health"/> changed.</summary>
    public event Action<HealthReport>? HealthChanged;

    /// <summary>A new snapshot; raised on a timer thread at most <see cref="SnapshotRate"/> times a second.</summary>
    public event Action<VehicleSnapshot>? SnapshotChanged;

    /// <summary><see cref="Limits"/> changed.</summary>
    public event Action<VehicleLimits>? LimitsChanged;

    /// <summary>The navigation mode group of a mode in the autopilot's naming; null without events metadata or for an unknown mode.</summary>
    public int? ModeGroupOf(string? modeName) =>
        modeName is not null && EventDefinitions is { } events && Session.Profile.TryGetSetModeParams(modeName, out _, out var main, out var sub)
            ? events.ModeGroupOf(((uint)main << 16) | ((uint)sub << 24))
            : null;

    internal void SetHealth(HealthReport report)
    {
        lock (_lock)
        {
            _health = report;
        }

        HealthChanged?.Invoke(report);
    }

    internal void RaiseMessage(NotificationSeverity severity, string text) => Message?.Invoke(severity, text);

    /// <summary>
    /// PX4 ends a STATUSTEXT with a tab when it also sends it as an event; it is dropped once the events are interpreted, so it is said once.
    /// </summary>
    private void OnStatusText(MavSeverity severity, string text)
    {
        if (severity == MavSeverity.Debug || string.IsNullOrWhiteSpace(text) || (text.EndsWith('\t') && EventDefinitions is not null))
        {
            return;
        }

        RaiseMessage(severity switch
        {
            <= MavSeverity.Error => NotificationSeverity.Error,
            MavSeverity.Warning => NotificationSeverity.Warning,
            _ => NotificationSeverity.Info
        }, text.Trim());
    }

    private void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    private void Publish()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }

        var snapshot = Build();
        lock (_lock)
        {
            _snapshot = snapshot;
        }

        SnapshotChanged?.Invoke(snapshot);
    }

    private VehicleSnapshot Build()
    {
        var status = Session.Status;
        var position = status.Position;
        var state = status.State;
        return new VehicleSnapshot
        {
            Id = Id,
            Name = Name,
            TimeUtc = _time.GetUtcNow().UtcDateTime,
            IsConnected = Session.IsConnected,
            Position = position is null ? null : new GeoPoint(position.Latitude, position.Longitude),
            AltitudeAmslMeters = position?.AltitudeAmsl,
            RelativeAltitudeMeters = position?.RelativeAltitude,
            HeadingDegrees = position?.HeadingDegrees ?? status.Attitude?.YawDegrees,
            CourseDegrees = position?.CourseDegrees,
            GroundSpeedMps = position?.GroundSpeed,
            AirspeedMps = position?.Airspeed,
            ClimbRateMps = position?.ClimbRate,
            RollDegrees = status.Attitude?.RollDegrees,
            PitchDegrees = status.Attitude?.PitchDegrees,
            IsArmed = state.IsArmed,
            Mode = state.Mode,
            IsLanded = state.IsLanded,
            BatteryPercent = _battery,
            Fix = status.Gps?.Fix switch
            {
                null or GpsFix.NoGps => PositionFix.None,
                GpsFix.NoFix => PositionFix.NoFix,
                GpsFix.Fix2D => PositionFix.Fix2D,
                GpsFix.Fix3D => PositionFix.Fix3D,
                _ => PositionFix.Precise
            },
            Satellites = status.Gps?.SatellitesVisible,
            Home = status.Home is { } home ? new GeoPoint(home.Latitude, home.Longitude) : null,
            HomeAltitudeAmslMeters = status.Home?.AltitudeAmsl,
            ThrottlePercent = position?.ThrottlePercent,
            Battery = status.BatteryState is { } battery
                ? new BatteryState(battery.VoltageVolts, battery.CurrentAmps, battery.ConsumedMah, battery.TimeRemainingSeconds, battery.CellCount)
                : null,
            Navigation = status.Navigation is { } nav
                ? new NavigationState(nav.WaypointDistanceMeters, nav.TargetBearingDegrees, nav.AltitudeErrorMeters, nav.AirspeedErrorMps, nav.CrossTrackErrorMeters)
                : null,
            Hdop = status.Gps?.Hdop,
            Estimator = status.Estimator is { } e
                ? new EstimatorHealth(e.Velocity, e.HorizontalPosition, e.VerticalPosition, e.Magnetometer, e.HeightAboveGround, e.Airspeed)
                : null,
            Vibration = status.Vibration is { } v ? new VibrationState(Math.Max(v.X, Math.Max(v.Y, v.Z)), v.Clipping) : null,
            ArmedSinceUtc = state.IsArmed ? _armedSince : null
        };
    }

    private void UpdateLimits()
    {
        if (Session.Profile.Name != "px4" || VehicleLimitsMapper.FromPx4(name => Parameters[name]) is not { } limits)
        {
            return;
        }

        lock (_lock)
        {
            if (limits == _limits)
            {
                return;
            }

            _limits = limits;
        }

        LimitsChanged?.Invoke(limits);
    }

    /// <summary>Stops publishing snapshots.</summary>
    public void Dispose() => _timer.Dispose();
}
