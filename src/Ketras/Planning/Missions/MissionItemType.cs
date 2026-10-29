namespace Ketras.Planning.Missions;

/// <summary>Every mission item the station can plan, grouped by the flight phase it belongs to.</summary>
public enum MissionItemType
{
    /// <summary>Climb out to an altitude.</summary>
    Takeoff,

    /// <summary>Fly to a point and continue.</summary>
    Waypoint,

    /// <summary>Orbit until the operator intervenes.</summary>
    LoiterUnlimited,

    /// <summary>Orbit for a set time.</summary>
    LoiterTime,

    /// <summary>Orbit while climbing or descending to an altitude.</summary>
    LoiterToAltitude,

    /// <summary>Point the camera at a location.</summary>
    RegionOfInterest,

    /// <summary>Set the target airspeed.</summary>
    ChangeSpeed,

    /// <summary>Capture one frame.</summary>
    TakePhoto,

    /// <summary>Start video recording.</summary>
    StartVideo,

    /// <summary>Stop video recording.</summary>
    StopVideo,

    /// <summary>Trigger the camera every N metres.</summary>
    CameraTriggerDistance,

    /// <summary>Hold the plan for a set time.</summary>
    DelaySeconds,

    /// <summary>Repeat an earlier part of the plan.</summary>
    DoJump,

    /// <summary>Fixed-wing landing: an orbit, its tangent, then a straight final to touchdown.</summary>
    LandingPattern,

    /// <summary>Land at a point.</summary>
    Land,

    /// <summary>Return home and finish the plan.</summary>
    ReturnToLaunch
}

/// <summary>Flight phase an item belongs to; drives list grouping and ordering rules.</summary>
public enum MissionPhase
{
    /// <summary>Before takeoff; holds only the mission settings.</summary>
    PreFlight,

    /// <summary>Takeoff.</summary>
    Takeoff,

    /// <summary>Everything between takeoff and landing.</summary>
    Cruise,

    /// <summary>Landing and return.</summary>
    Landing
}

/// <summary>What the vehicle does after the last item. A landing is planned as items (landing pattern, land), never as an end action.</summary>
public enum MissionEndAction
{
    /// <summary>Hold at the last position.</summary>
    Hold,

    /// <summary>Return to launch.</summary>
    ReturnToLaunch
}

/// <summary>Catalog facts per item type: whether it has a map position and which phase it belongs to.</summary>
public static class MissionItemCatalog
{
    private static readonly Dictionary<MissionItemType, (bool UsesLocation, MissionPhase Phase)> Table = new()
    {
        [MissionItemType.Takeoff] = (true, MissionPhase.Takeoff),
        [MissionItemType.Waypoint] = (true, MissionPhase.Cruise),
        [MissionItemType.LoiterUnlimited] = (true, MissionPhase.Cruise),
        [MissionItemType.LoiterTime] = (true, MissionPhase.Cruise),
        [MissionItemType.LoiterToAltitude] = (true, MissionPhase.Cruise),
        [MissionItemType.RegionOfInterest] = (true, MissionPhase.Cruise),
        [MissionItemType.ChangeSpeed] = (false, MissionPhase.Cruise),
        [MissionItemType.TakePhoto] = (false, MissionPhase.Cruise),
        [MissionItemType.StartVideo] = (false, MissionPhase.Cruise),
        [MissionItemType.StopVideo] = (false, MissionPhase.Cruise),
        [MissionItemType.CameraTriggerDistance] = (false, MissionPhase.Cruise),
        [MissionItemType.DelaySeconds] = (false, MissionPhase.Cruise),
        [MissionItemType.DoJump] = (false, MissionPhase.Cruise),
        [MissionItemType.LandingPattern] = (true, MissionPhase.Landing),
        [MissionItemType.Land] = (true, MissionPhase.Landing),
        [MissionItemType.ReturnToLaunch] = (false, MissionPhase.Landing)
    };

    /// <summary>True when the item occupies a point on the map; its altitude is then relative to the planned home.</summary>
    public static bool UsesLocation(MissionItemType type) => Table[type].UsesLocation;

    /// <summary>Flight phase of the item type.</summary>
    public static MissionPhase PhaseOf(MissionItemType type) => Table[type].Phase;

    /// <summary>Item types of a phase, in catalog order.</summary>
    public static IEnumerable<MissionItemType> TypesInPhase(MissionPhase phase) =>
        Table.Where(pair => pair.Value.Phase == phase).Select(pair => pair.Key);
}
