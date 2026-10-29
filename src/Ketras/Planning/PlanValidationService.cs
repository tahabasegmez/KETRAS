using Ketras.Environment;
using Ketras.Mavlink.Protocols;
using Ketras.Model.Missions;
using Ketras.Model.Planning;
using Ketras.Model.Vehicles;
using Ketras.Notifications;
using Ketras.Settings;
using Ketras.Terrain;
using Ketras.Vehicles;
using Microsoft.Extensions.Hosting;

namespace Ketras.Planning;

/// <summary>
/// Checks the plan whenever it, the vehicle's limits or battery parameters, the energy settings, the terrain or the
/// geofence zones change, once edits have settled for <see cref="Settle"/>. Each finding is a notification condition
/// (source "Plan") that is withdrawn when it clears. Also estimates the flight (<see cref="Estimate"/>, in the vehicle's
/// wind when it reports one) and its energy budget before flight (<see cref="Budget"/>: the battery's charge now, the
/// entered cruise current). Thread-safe.
/// </summary>
public sealed class PlanValidationService : IHostedService, IDisposable
{
    /// <summary>Quiet time after the last change before the plan is checked.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(600);

    private readonly PlanStore _plans;
    private readonly TerrainService _terrain;
    private readonly EnvironmentStore _environment;
    private readonly VehicleRegistry _vehicles;
    private readonly NotificationStore _notifications;
    private readonly SettingsStore _settings;
    private readonly ITimer _timer;
    private readonly object _lock = new();
    private IReadOnlyList<PlanFinding> _findings = [];
    private MissionEstimate _estimate = new([], false);
    private EnergyBudget? _budget;
    private Vehicle? _limitsSource;

    /// <summary>Starts following the plan and its inputs.</summary>
    public PlanValidationService(PlanStore plans, TerrainService terrain, EnvironmentStore environment, VehicleRegistry vehicles,
        NotificationStore notifications, SettingsStore settings, TimeProvider time)
    {
        _settings = settings;
        settings.Changed += key =>
        {
            if (key == EnergySettings.CruiseCurrent.Key || key == EnergySettings.Capacity.Key)
            {
                Schedule();
            }
        };
        _plans = plans;
        _terrain = terrain;
        _environment = environment;
        _vehicles = vehicles;
        _notifications = notifications;
        _timer = time.CreateTimer(_ => Check(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        plans.Changed += (_, _) => Schedule();
        terrain.GridChanged += _ => Schedule();
        environment.Changed += change =>
        {
            if (change.HasFlag(EnvironmentChange.Zones))
            {
                Schedule();
            }
        };
        vehicles.ActiveChanged += Follow;
        Follow(vehicles.Active);
    }

    /// <summary>The findings of the last check.</summary>
    public IReadOnlyList<PlanFinding> Findings
    {
        get
        {
            lock (_lock)
            {
                return _findings;
            }
        }
    }

    /// <summary>The flight time and distance of the plan at the last check.</summary>
    public MissionEstimate Estimate
    {
        get
        {
            lock (_lock)
            {
                return _estimate;
            }
        }
    }

    /// <summary>The plan's energy budget at the last check; null without a capacity or an entered cruise current.</summary>
    public EnergyBudget? Budget
    {
        get
        {
            lock (_lock)
            {
                return _budget;
            }
        }
    }

    /// <summary><see cref="Findings"/> changed (and with them <see cref="Estimate"/> and <see cref="Budget"/>).</summary>
    public event Action<IReadOnlyList<PlanFinding>>? Changed;

    /// <summary>Checks now instead of after the settle time.</summary>
    public void Check()
    {
        var vehicle = _vehicles.Active;
        var limits = vehicle?.Limits ?? VehicleLimits.Default;
        _plans.Limits = limits;
        var plan = _plans.Current;
        var wind = vehicle?.Session.Status.Wind;
        var estimate = MissionTimeCalculator.Estimate(plan.Mission, wind?.NorthMps ?? 0, wind?.EastMps ?? 0);
        var energy = EnergySettings.Resolve(vehicle, _settings);
        var budget = estimate.Legs.Count == 0 || estimate.IsOpenEnded ? null
            : EnergyCalculator.Budget(energy.CapacityMah, vehicle?.Snapshot.BatteryPercent ?? 100, energy.ReserveFraction, estimate.Seconds,
                energy.CruiseCurrentAmps, CurrentSource.Entered);
        IReadOnlyList<PlanFinding> findings =
            [.. PlanRules.Validate(plan, limits, _terrain.Grid, _environment.GeofenceZones()), .. MissionBudgetRules.Validate(estimate, budget)];
        lock (_lock)
        {
            _findings = findings;
            _estimate = estimate;
            _budget = budget;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var finding in findings)
        {
            var key = $"{finding.Code}:{finding.ItemNumber}:{finding.FenceId}";
            if (keys.Add(key))
            {
                _notifications.SetCondition(PlanStore.Source, key, NotificationSeverity.Warning, finding.Message);
            }
        }

        _notifications.ClearConditionsExcept(PlanStore.Source, keys);
        Changed?.Invoke(findings);
    }

    private void Schedule() => _timer.Change(Settle, Timeout.InfiniteTimeSpan);

    private void Follow(Vehicle? vehicle)
    {
        if (_limitsSource is not null)
        {
            _limitsSource.LimitsChanged -= OnLimits;
            _limitsSource.Parameters.ValueChanged -= OnParameter;
            _limitsSource.Parameters.Loaded -= OnParametersLoaded;
        }

        _limitsSource = vehicle;
        if (vehicle is not null)
        {
            vehicle.LimitsChanged += OnLimits;
            vehicle.Parameters.ValueChanged += OnParameter;
            vehicle.Parameters.Loaded += OnParametersLoaded;
        }

        Schedule();
    }

    private void OnLimits(VehicleLimits limits) => Schedule();

    private void OnParameter(ParameterEntry entry)
    {
        if (EnergySettings.Parameters.Contains(entry.Name))
        {
            Schedule();
        }
    }

    private void OnParametersLoaded(ParameterListResult result) => Schedule();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Schedule();
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
}
