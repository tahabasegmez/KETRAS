using Ketras.Geo;
using Ketras.Vehicles;

namespace Ketras.Planning.Missions;

/// <summary>Outcome of an edit: the resulting mission, and why nothing changed when the edit was refused.</summary>
/// <param name="Mission">The mission after the edit (the unchanged one when refused).</param>
/// <param name="Applied">False when the rules refused the edit or the item was not found.</param>
/// <param name="Reason">Why it was refused, in operator-facing English.</param>
/// <param name="ItemId">The item the edit created or touched.</param>
public sealed record MissionEdit(Mission Mission, bool Applied, string? Reason = null, Guid? ItemId = null);

/// <summary>
/// Pure edit operations on a <see cref="Mission"/>: each returns a new mission with jump targets kept in sync. The
/// phase rules of <see cref="MissionRules"/> apply: items go to the end of their phase and never cross into another.
/// </summary>
public static class MissionEditor
{
    /// <summary>A loaded or replaced mission with its jump targets resolved.</summary>
    public static Mission Normalize(Mission mission) => MissionRules.SyncJumpTargets(mission);

    /// <summary>Adds a new item at the end of its phase, unless the rules refuse it.</summary>
    public static MissionEdit Add(Mission mission, MissionItemType type, GeoPoint? location, MissionItemOptions? options = null, VehicleLimits? limits = null)
    {
        if (!MissionRules.CanAdd(mission, type, out var reason))
        {
            return new MissionEdit(mission, false, reason);
        }

        var item = MissionItemFactory.Create(type, location, options, mission.Settings, limits);
        var next = mission with { Items = mission.Items.Insert(MissionRules.InsertIndexFor(mission, type), item) };
        return new MissionEdit(Normalize(next), true, ItemId: item.Id);
    }

    /// <summary>Removes an item; jumps to it lose their target.</summary>
    public static MissionEdit Remove(Mission mission, Guid id)
    {
        var index = mission.IndexOf(id);
        return index < 0
            ? new MissionEdit(mission, false, "The item is not in the plan.")
            : new MissionEdit(Normalize(mission with { Items = mission.Items.RemoveAt(index) }), true, ItemId: id);
    }

    /// <summary>Moves an item by <paramref name="delta"/> places within its own phase.</summary>
    public static MissionEdit Move(Mission mission, Guid id, int delta)
    {
        var index = mission.IndexOf(id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= mission.Items.Count)
        {
            return new MissionEdit(mission, false, "The item cannot move further.");
        }

        if (mission.Items[target].Phase != mission.Items[index].Phase)
        {
            return new MissionEdit(mission, false, "An item stays within its flight phase.");
        }

        var moved = mission.Items.RemoveAt(index).Insert(target, mission.Items[index]);
        return new MissionEdit(Normalize(mission with { Items = moved }), true, ItemId: id);
    }

    /// <summary>Replaces an item with the result of <paramref name="change"/>; its id and type are kept.</summary>
    public static MissionEdit Update(Mission mission, Guid id, Func<MissionItem, MissionItem> change)
    {
        var index = mission.IndexOf(id);
        if (index < 0)
        {
            return new MissionEdit(mission, false, "The item is not in the plan.");
        }

        var original = mission.Items[index];
        var updated = change(original) with { Id = original.Id, Type = original.Type };
        return new MissionEdit(Normalize(mission with { Items = mission.Items.SetItem(index, updated) }), true, ItemId: id);
    }

    /// <summary>Changes the mission settings.</summary>
    public static Mission UpdateSettings(Mission mission, Func<MissionSettings, MissionSettings> change) =>
        mission with { Settings = change(mission.Settings) };

    /// <summary>Removes every item; the settings stay.</summary>
    public static Mission Clear(Mission mission) => mission with { Items = [] };
}
