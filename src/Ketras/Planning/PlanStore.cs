using Ketras.Geo;
using Ketras.Notifications;
using Ketras.Planning.Fences;
using Ketras.Planning.Missions;
using Ketras.Vehicles;

namespace Ketras.Planning;

/// <summary>What part of the plan changed.</summary>
[Flags]
public enum PlanChange
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The mission's items or settings.</summary>
    Mission = 1,

    /// <summary>The fences.</summary>
    Fences = 2
}

/// <summary>
/// Store of the plan being edited, with or without a vehicle. Every edit goes through here, so the map, the panels,
/// the upload and the checks always see one plan. Edits the rules refuse are reported as notifications. Thread-safe;
/// <see cref="Changed"/> is raised on the editing thread.
/// </summary>
public sealed class PlanStore(NotificationStore notifications)
{
    /// <summary>Source name of the plan's notifications.</summary>
    public const string Source = "Plan";

    private readonly object _lock = new();
    private Plan _plan = Plan.Empty;
    private VehicleLimits _limits = VehicleLimits.Default;

    /// <summary>The plan as it stands.</summary>
    public Plan Current
    {
        get
        {
            lock (_lock)
            {
                return _plan;
            }
        }
    }

    /// <summary>The limits new items are created with (the active vehicle's).</summary>
    public VehicleLimits Limits
    {
        get
        {
            lock (_lock)
            {
                return _limits;
            }
        }

        set
        {
            lock (_lock)
            {
                _limits = value;
            }
        }
    }

    /// <summary>The plan changed: the new plan and what changed.</summary>
    public event Action<Plan, PlanChange>? Changed;

    /// <summary>Replaces the whole plan (e.g. from a file); jump targets are resolved.</summary>
    public void Replace(Plan plan) =>
        Apply(_ => plan with { Mission = MissionEditor.Normalize(plan.Mission) }, PlanChange.Mission | PlanChange.Fences);

    /// <summary>Adds a mission item at the end of its phase; a refusal is reported and returns null.</summary>
    public Guid? AddItem(MissionItemType type, GeoPoint? location, MissionItemOptions? options = null)
    {
        MissionEdit? edit = null;
        Apply(plan =>
        {
            edit = MissionEditor.Add(plan.Mission, type, location, options, Limits);
            return edit.Applied ? plan with { Mission = edit.Mission } : plan;
        }, PlanChange.Mission);
        return Report(edit!);
    }

    /// <summary>Removes a mission item.</summary>
    public bool RemoveItem(Guid id) => Edit(mission => MissionEditor.Remove(mission, id));

    /// <summary>Moves a mission item within its phase.</summary>
    public bool MoveItem(Guid id, int delta) => Edit(mission => MissionEditor.Move(mission, id, delta));

    /// <summary>Changes one mission item.</summary>
    public bool UpdateItem(Guid id, Func<MissionItem, MissionItem> change) => Edit(mission => MissionEditor.Update(mission, id, change));

    /// <summary>Changes the mission settings.</summary>
    public void UpdateSettings(Func<MissionSettings, MissionSettings> change) => Apply(plan =>
    {
        var settings = change(plan.Mission.Settings);
        return ReferenceEquals(settings, plan.Mission.Settings) ? plan : plan with { Mission = plan.Mission with { Settings = settings } };
    }, PlanChange.Mission);

    /// <summary>
    /// Changes what the plan's altitudes are measured from, converting every item so it flies the same heights above
    /// sea level (<see cref="MissionAltitudes.Convert"/>). Returns null when done, or the 1-based number of the item whose
    /// terrain is unknown, in which case nothing changes.
    /// </summary>
    public int? SetAltitudeReference(AltitudeReference reference, Func<GeoPoint, double?>? groundAt)
    {
        int? failed = null;
        Apply(plan =>
        {
            if (MissionAltitudes.Convert(plan.Mission, reference, groundAt, out failed) is not { } converted)
            {
                return plan;
            }

            return ReferenceEquals(converted, plan.Mission) ? plan : plan with { Mission = converted };
        }, PlanChange.Mission);
        return failed;
    }

    /// <summary>Removes every mission item.</summary>
    public void ClearMission() => Apply(plan => plan.Mission.Items.IsEmpty ? plan : plan with { Mission = MissionEditor.Clear(plan.Mission) }, PlanChange.Mission);

    /// <summary>Adds a fence, or replaces the one with its id.</summary>
    public void UpsertFence(Fence fence) => Apply(plan =>
    {
        var index = plan.Fences.FindIndex(f => f.Id == fence.Id);
        return plan with { Fences = index >= 0 ? plan.Fences.SetItem(index, fence) : plan.Fences.Add(fence) };
    }, PlanChange.Fences);

    /// <summary>Removes a fence.</summary>
    public bool RemoveFence(string id)
    {
        var removed = false;
        Apply(plan =>
        {
            var index = plan.Fences.FindIndex(f => f.Id == id);
            removed = index >= 0;
            return removed ? plan with { Fences = plan.Fences.RemoveAt(index) } : plan;
        }, PlanChange.Fences);
        return removed;
    }

    /// <summary>Removes every fence.</summary>
    public void ClearFences() => Apply(plan => plan.Fences.IsEmpty ? plan : plan with { Fences = [] }, PlanChange.Fences);

    /// <summary>An id for a new fence that no fence uses.</summary>
    public string NextFenceId() => Fence.NextId(Current.Fences);

    /// <summary>Applies <paramref name="change"/> atomically; raises <see cref="Changed"/> when the plan differs.</summary>
    public void Apply(Func<Plan, Plan> change, PlanChange what)
    {
        Plan next;
        lock (_lock)
        {
            next = change(_plan);
            if (ReferenceEquals(next, _plan))
            {
                return;
            }

            _plan = next;
        }

        Changed?.Invoke(next, what);
    }

    private bool Edit(Func<Mission, MissionEdit> edit)
    {
        MissionEdit? result = null;
        Apply(plan =>
        {
            result = edit(plan.Mission);
            return result.Applied ? plan with { Mission = result.Mission } : plan;
        }, PlanChange.Mission);
        return Report(result!) is not null;
    }

    private Guid? Report(MissionEdit edit)
    {
        if (!edit.Applied)
        {
            notifications.Notify(Source, NotificationSeverity.Warning, edit.Reason ?? "The edit was not applied.");
            return null;
        }

        return edit.ItemId;
    }
}
