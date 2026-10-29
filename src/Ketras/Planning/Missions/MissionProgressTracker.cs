using Ketras.Commands;
using Ketras.Model.Vehicles;
using Ketras.Vehicles;

namespace Ketras.Planning.Missions;

/// <summary>
/// The mission each vehicle carries since this station uploaded it, and how far the active vehicle got: MISSION_CURRENT
/// mapped back through the upload's expansion to the plan's item index, and the execution state from arming and mode.
/// Thread-safe; <see cref="Changed"/> is raised on the link's dispatch thread or the caller's.
/// </summary>
public sealed class MissionProgressTracker : IDisposable
{
    private readonly VehicleRegistry _vehicles;
    private readonly object _lock = new();
    private readonly Dictionary<VehicleId, EncodedMission> _uploaded = [];
    private Vehicle? _followed;
    private MissionProgress _progress = MissionProgress.Idle;

    /// <summary>Follows the active vehicle.</summary>
    public MissionProgressTracker(VehicleRegistry vehicles)
    {
        _vehicles = vehicles;
        vehicles.ActiveChanged += Follow;
        vehicles.VehicleRemoved += vehicle =>
        {
            lock (_lock)
            {
                _uploaded.Remove(vehicle.Id);
            }
        };
        Follow(vehicles.Active);
    }

    /// <summary>The active vehicle's mission progress.</summary>
    public MissionProgress Progress
    {
        get
        {
            lock (_lock)
            {
                return _progress;
            }
        }
    }

    /// <summary><see cref="Progress"/> changed.</summary>
    public event Action<MissionProgress>? Changed;

    /// <summary>The mission last uploaded to <paramref name="vehicle"/> by this station, if any.</summary>
    public EncodedMission? Uploaded(VehicleId vehicle)
    {
        lock (_lock)
        {
            return _uploaded.GetValueOrDefault(vehicle);
        }
    }

    /// <summary>Records a successful upload.</summary>
    public void SetUploaded(VehicleId vehicle, EncodedMission mission)
    {
        lock (_lock)
        {
            _uploaded[vehicle] = mission;
        }

        Update();
    }

    private void Follow(Vehicle? vehicle)
    {
        if (_followed is not null)
        {
            _followed.Session.Status.StateChanged -= OnState;
            _followed.Session.Status.MissionCurrentChanged -= OnCurrent;
        }

        _followed = vehicle;
        if (vehicle is not null)
        {
            vehicle.Session.Status.StateChanged += OnState;
            vehicle.Session.Status.MissionCurrentChanged += OnCurrent;
        }

        Update();
    }

    private void OnState(Mavlink.Vehicles.VehicleState state) => Update();

    private void OnCurrent(int sequence, int total) => Update();

    private void Update()
    {
        var vehicle = _followed;
        MissionProgress next;
        if (vehicle is null)
        {
            next = MissionProgress.Idle;
        }
        else
        {
            var status = vehicle.Session.Status;
            var state = status.State;
            var missionMode = AutopilotModes.For(vehicle.Session.Profile.Name).Mission;
            var execution = !state.IsArmed ? MissionExecution.Idle
                : string.Equals(state.Mode, missionMode, StringComparison.OrdinalIgnoreCase) ? MissionExecution.Executing
                : MissionExecution.Paused;
            int? index = null;
            lock (_lock)
            {
                var sequence = status.MissionCurrent;
                if (_uploaded.GetValueOrDefault(vehicle.Id) is { } uploaded && sequence >= 0 && sequence < uploaded.PlanIndexOfSequence.Count
                    && uploaded.PlanIndexOfSequence[sequence] >= 0)
                {
                    index = uploaded.PlanIndexOfSequence[sequence];
                }
            }

            next = new MissionProgress(execution, index, state.Mode);
        }

        lock (_lock)
        {
            if (next == _progress)
            {
                return;
            }

            _progress = next;
        }

        Changed?.Invoke(next);
    }

    /// <inheritdoc />
    public void Dispose() => Follow(null);
}

