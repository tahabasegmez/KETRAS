using Ketras.Model.Geo;
using Ketras.Model.Vehicles;
using Ketras.Station.Links;
using Ketras.Station.Missions;
using Ketras.Station.Notifications;
using Ketras.Station.Settings;
using Microsoft.Extensions.Hosting;

namespace Ketras.Station.Vehicles;

/// <summary>The active vehicle's flight as the station reads it, once a second.</summary>
/// <param name="Link">The link's recent quality.</param>
/// <param name="Checks">The vehicle's health checks (<see cref="FlightHealthRules"/>) and, in flight, the flight checks.</param>
/// <param name="NextItemSeconds">Time to the active mission item at the ground speed; null when not flying the mission.</param>
/// <param name="EndSeconds">Time to the end of the mission, the return home included; null when not flying it.</param>
/// <param name="HomeDistanceMeters">Distance to the home.</param>
/// <param name="HomeBearingDegrees">Bearing to the home.</param>
/// <param name="Energy">The budget of what is left to fly; null without a capacity or a draw.</param>
/// <param name="FlightTime">Time since arming; null while disarmed.</param>
public sealed record FlightStatus(
    LinkQuality? Link,
    IReadOnlyList<FlightCheck> Checks,
    double? NextItemSeconds,
    double? EndSeconds,
    double? HomeDistanceMeters,
    double? HomeBearingDegrees,
    EnergyBudget? Energy,
    TimeSpan? FlightTime)
{
    /// <summary>Nothing known.</summary>
    public static FlightStatus None { get; } = new(null, [], null, null, null, null, null, null);
}

/// <summary>
/// Worker that watches the active vehicle once a second: the link's loss over the last <see cref="LinkWindow"/> (from
/// the connection's sequence-gap counters) and its radio, the health checks of <see cref="FlightHealthRules"/>, and in
/// flight the progress (time to the next item and to the end, the way home) and the energy left for it (the average
/// current measured since arming, the entered cruise current until a minute has passed). Checks that warn are
/// notification conditions (source <see cref="Source"/>), withdrawn when they clear; in flight these join them, each once
/// it has held for its time: the altitude error past <see cref="AltitudeErrorMeters"/> for 10 s, the cross-track error
/// past twice the loiter radius for 10 s, full throttle while too slow for 5 s, and an energy margin below zero.
/// </summary>
public sealed class FlightMonitorWorker : IHostedService, IDisposable
{
    /// <summary>The notification source of the flight conditions.</summary>
    public const string Source = "Flight";

    /// <summary>The window the link loss is measured over.</summary>
    public static readonly TimeSpan LinkWindow = TimeSpan.FromSeconds(10);

    /// <summary>An altitude error past this, m, is a condition.</summary>
    public const double AltitudeErrorMeters = 30;

    /// <summary>Throttle from this, %, while slower than the setpoint by <see cref="SlowMps"/> is a condition.</summary>
    public const double FullThrottlePercent = 95;

    /// <summary>See <see cref="FullThrottlePercent"/>.</summary>
    public const double SlowMps = 2;

    private readonly VehicleRegistry _vehicles;
    private readonly ConnectionService _connections;
    private readonly PlanStore _plans;
    private readonly PlanValidationService _validation;
    private readonly MissionProgressTracker _progress;
    private readonly NotificationStore _notifications;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly object _lock = new();
    private readonly Queue<(DateTime At, long Received, long Lost, long Clipping)> _samples = new();
    private readonly Dictionary<string, DateTime> _since = [];
    private FlightStatus _status = FlightStatus.None;
    private Vehicle? _vehicle;
    private double? _consumedAtArm;

    /// <summary>Follows the active vehicle and its link.</summary>
    public FlightMonitorWorker(VehicleRegistry vehicles, ConnectionService connections, PlanStore plans, PlanValidationService validation,
        MissionProgressTracker progress, NotificationStore notifications, SettingsStore settings, TimeProvider time)
    {
        _vehicles = vehicles;
        _connections = connections;
        _plans = plans;
        _validation = validation;
        _progress = progress;
        _notifications = notifications;
        _settings = settings;
        _time = time;
        _timer = time.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The latest reading.</summary>
    public FlightStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary><see cref="Status"/> changed; raised on a timer thread.</summary>
    public event Action<FlightStatus>? Changed;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _timer.Dispose();

    /// <summary>Reads the vehicle now; the timer calls it every second.</summary>
    public void Tick()
    {
        FlightStatus status;
        lock (_lock)
        {
            status = Read(_time.GetUtcNow().UtcDateTime);
            _status = status;
        }

        Changed?.Invoke(status);
    }

    private FlightStatus Read(DateTime now)
    {
        var vehicle = _vehicles.Active;
        if (!ReferenceEquals(vehicle, _vehicle))
        {
            _vehicle = vehicle;
            _samples.Clear();
            _since.Clear();
            _consumedAtArm = null;
        }

        if (vehicle is null)
        {
            _notifications.ClearConditionsExcept(Source, new HashSet<string>());
            return FlightStatus.None;
        }

        var snapshot = vehicle.Snapshot;
        var (link, newClipping) = Sample(now, snapshot);
        var energyInputs = EnergySettings.Resolve(vehicle, _settings);
        var checks = new List<FlightCheck>(FlightHealthRules.Evaluate(snapshot, link, energyInputs.Limits, newClipping));

        var flying = snapshot.IsArmed && snapshot.IsLanded != true;
        if (snapshot.IsArmed)
        {
            _consumedAtArm ??= snapshot.Battery?.ConsumedMah;
        }
        else
        {
            _consumedAtArm = null;
        }

        double? homeDistance = null, homeBearing = null;
        if (snapshot.Position is { } position && snapshot.Home is { } home)
        {
            homeDistance = GeoMath.DistanceMeters(position, home);
            homeBearing = GeoMath.BearingDegrees(position, home);
        }

        double? next = null, end = null;
        var progress = _progress.Progress;
        if (flying && progress is { State: MissionExecution.Executing, CurrentItemIndex: { } current } && snapshot.Navigation is { } nav)
        {
            var groundSpeed = Math.Max(snapshot.GroundSpeedMps ?? 0, 1);
            next = nav.WaypointDistanceMeters / groundSpeed;
            end = next + _validation.Estimate.SecondsFrom(current + 1);
        }
        else if (flying && homeDistance is { } distance)
        {
            end = distance / Math.Max(snapshot.GroundSpeedMps ?? 0, 1);
        }

        var flightTime = snapshot.ArmedSinceUtc is { } armed ? now - armed : (TimeSpan?)null;
        EnergyBudget? energy = null;
        if (flying && end is { } remaining && snapshot.BatteryPercent is { } percent)
        {
            var measured = EnergyCalculator.AverageCurrent(_consumedAtArm, snapshot.Battery?.ConsumedMah, flightTime ?? TimeSpan.Zero);
            energy = EnergyCalculator.Budget(energyInputs.CapacityMah, percent, energyInputs.ReserveFraction, remaining,
                measured ?? energyInputs.CruiseCurrentAmps, measured is null ? CurrentSource.Entered : CurrentSource.Measured);
        }

        if (flying)
        {
            checks.AddRange(FlightChecks(now, snapshot, vehicle.Limits, energy));
        }
        else
        {
            _since.Clear();
        }

        Publish(checks);
        return new FlightStatus(link, checks, next, end, homeDistance, homeBearing, energy, flightTime);
    }

    private (LinkQuality? Link, long NewClipping) Sample(DateTime now, VehicleSnapshot snapshot)
    {
        var statistics = _connections.Connection?.Statistics;
        var clipping = snapshot.Vibration?.Clipping ?? 0;
        if (statistics is not null)
        {
            _samples.Enqueue((now, statistics.PacketsReceived, statistics.PacketsLost, clipping));
        }

        while (_samples.Count > 1 && now - _samples.Peek().At > LinkWindow)
        {
            _samples.Dequeue();
        }

        if (statistics is null || _samples.Count < 2)
        {
            return (null, 0);
        }

        var first = _samples.Peek();
        var received = statistics.PacketsReceived - first.Received;
        var lost = statistics.PacketsLost - first.Lost;
        var radio = statistics.Radio is { } r && now - r.ReceivedUtc < LinkWindow ? r : null;
        var link = new LinkQuality(received + lost < 10 ? null : lost * 100.0 / (received + lost),
            radio is null || radio.Rssi == byte.MaxValue ? null : radio.Rssi, radio is null || radio.RemoteRssi == byte.MaxValue ? null : radio.RemoteRssi);
        return (link, Math.Max(0, clipping - first.Clipping));
    }

    private IEnumerable<FlightCheck> FlightChecks(DateTime now, VehicleSnapshot snapshot, VehicleLimits limits, EnergyBudget? energy)
    {
        if (snapshot.Navigation is { } nav)
        {
            if (Held("flight.altitude", Math.Abs(nav.AltitudeErrorMeters) > AltitudeErrorMeters, TimeSpan.FromSeconds(10), now))
            {
                yield return new FlightCheck("flight.altitude", "Altitude", CheckLevel.Warning,
                    $"{Math.Abs(nav.AltitudeErrorMeters):F0} m {(nav.AltitudeErrorMeters > 0 ? "below" : "above")} the target altitude");
            }

            var trackLimit = 2 * limits.DefaultLoiterRadiusMeters;
            if (Held("flight.track", Math.Abs(nav.CrossTrackErrorMeters) > trackLimit, TimeSpan.FromSeconds(10), now))
            {
                yield return new FlightCheck("flight.track", "Track", CheckLevel.Warning,
                    $"{Math.Abs(nav.CrossTrackErrorMeters):F0} m off the planned line (more than {trackLimit:F0} m)");
            }

            if (Held("flight.power", snapshot.ThrottlePercent >= FullThrottlePercent && nav.AirspeedErrorMps <= -SlowMps, TimeSpan.FromSeconds(5), now))
            {
                yield return new FlightCheck("flight.power", "Power", CheckLevel.Warning,
                    $"full throttle and still {-nav.AirspeedErrorMps:F1} m/s below the target airspeed: underpowered, or climbing too steeply");
            }
        }

        if (energy is { MarginMah: < 0 } short_)
        {
            yield return new FlightCheck("flight.energy", "Energy", CheckLevel.Error,
                $"not enough battery to finish and return: {short_.NeededMah:F0} mAh needed, {short_.UsableMah:F0} mAh left above the low-battery threshold");
        }
    }

    private bool Held(string code, bool condition, TimeSpan hold, DateTime now)
    {
        if (!condition)
        {
            _since.Remove(code);
            return false;
        }

        if (!_since.TryGetValue(code, out var since))
        {
            _since[code] = since = now;
        }

        return now - since >= hold;
    }

    /// <summary>
    /// Holds a condition per failing check. Its text is fixed per check and level, so a condition is raised once and not
    /// again for every new number; the numbers are in <see cref="Status"/>.
    /// </summary>
    private void Publish(IReadOnlyList<FlightCheck> checks)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in checks.Where(c => c.Level is CheckLevel.Warning or CheckLevel.Error))
        {
            keys.Add(check.Code);
            _notifications.SetCondition(Source, check.Code, check.Level == CheckLevel.Error ? NotificationSeverity.Error : NotificationSeverity.Warning,
                Headline(check));
        }

        _notifications.ClearConditionsExcept(Source, keys);
    }

    private static string Headline(FlightCheck check) => (check.Code, check.Level) switch
    {
        ("vehicle.gps", CheckLevel.Error) => "GPS: no 3D fix",
        ("vehicle.gps", _) => "GPS: position quality is poor",
        ("vehicle.estimator", CheckLevel.Error) => "Estimator: a sensor is being rejected",
        ("vehicle.estimator", _) => "Estimator: a sensor disagrees",
        ("vehicle.vibration", CheckLevel.Error) => "Vibration is too high",
        ("vehicle.vibration", _) => "Vibration is high",
        ("vehicle.battery", CheckLevel.Error) => "Battery is empty",
        ("vehicle.battery", _) => "Battery is low",
        ("vehicle.link", CheckLevel.Error) => "Link: most packets are lost",
        ("vehicle.link", _) => "Link: packets are being lost",
        ("flight.altitude", _) => "Far from the target altitude",
        ("flight.track", _) => "Far off the planned line",
        ("flight.power", _) => "Full throttle and too slow",
        ("flight.energy", _) => "Not enough battery to finish and return",
        _ => check.Title
    };
}
