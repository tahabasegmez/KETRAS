using System.Collections.Immutable;
using Ketras.Model.Geo;

namespace Ketras.Model.Missions;

/// <summary>
/// One mission item. <see cref="Param1"/>–<see cref="Param4"/> carry the raw MAVLink payload;
/// what each means per type is in <see cref="MissionItemParameters"/> and <see cref="MissionItemValues"/>. Unset yaw
/// values are NaN.
/// </summary>
public sealed record MissionItem
{
    /// <summary>Stable identity across edits and reordering.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>What the item does.</summary>
    public required MissionItemType Type { get; init; }

    /// <summary>Continue to the next item when this one completes.</summary>
    public bool AutoContinue { get; init; } = true;

    /// <summary>MAVLink parameter 1.</summary>
    public double Param1 { get; init; }

    /// <summary>MAVLink parameter 2.</summary>
    public double Param2 { get; init; }

    /// <summary>MAVLink parameter 3.</summary>
    public double Param3 { get; init; }

    /// <summary>MAVLink parameter 4.</summary>
    public double Param4 { get; init; }

    /// <summary>Map position; null for items without one.</summary>
    public GeoPoint? Location { get; init; }

    /// <summary>Altitude in metres, in the plan's <see cref="MissionSettings.AltitudeReference"/> (see <see cref="MissionAltitudes"/>).</summary>
    public double AltitudeMeters { get; init; }

    /// <summary>Item a <see cref="MissionItemType.DoJump"/> jumps to; <see cref="Param1"/> follows its index.</summary>
    public Guid? JumpTargetId { get; init; }

    /// <summary>True when the item occupies a point on the map.</summary>
    public bool UsesLocation => MissionItemCatalog.UsesLocation(Type);

    /// <summary>Flight phase of the item.</summary>
    public MissionPhase Phase => MissionItemCatalog.PhaseOf(Type);
}

/// <summary>
/// Mission-wide settings. Not a MAVLink command: it configures the plan.
/// </summary>
public sealed record MissionSettings
{
    /// <summary>Where the vehicle takes off; (0, 0) until known.</summary>
    public GeoPoint PlannedHome { get; init; }

    /// <summary>Altitude of the planned home above mean sea level; with <see cref="AltitudeReference.RelativeToHome"/> item altitudes are relative to it.</summary>
    public double HomeAltitudeMeters { get; init; }

    /// <summary>Airspeed the plan is flown at until a speed change.</summary>
    public double CruiseSpeedMps { get; init; } = 18;

    /// <summary>Altitude new items start at.</summary>
    public double DefaultAltitudeMeters { get; init; } = 120;

    /// <summary>What happens after the last item.</summary>
    public MissionEndAction EndAction { get; init; } = MissionEndAction.ReturnToLaunch;

    /// <summary>What item altitudes (and a landing pattern's touchdown altitude) are measured from.</summary>
    public AltitudeReference AltitudeReference { get; init; } = AltitudeReference.RelativeToHome;

    /// <summary>How the vehicle uses its rangefinder when it lands; written to the vehicle with a mission that lands.</summary>
    public LandingSettings Landing { get; init; } = new();
}

/// <summary>What a plan's item altitudes are measured from.</summary>
public enum AltitudeReference
{
    /// <summary>Above the planned home (MAV_FRAME_GLOBAL_RELATIVE_ALT).</summary>
    RelativeToHome,

    /// <summary>Above mean sea level (MAV_FRAME_GLOBAL).</summary>
    AboveSeaLevel,

    /// <summary>Above the terrain under each item; sent as above mean sea level from the station's terrain data.</summary>
    AboveTerrain
}

/// <summary>How a fixed-wing landing uses the rangefinder's terrain estimate (PX4 FW_LND_USETER).</summary>
public enum LandingTerrainUse
{
    /// <summary>Not used: the land point's altitude is the ground.</summary>
    Off,

    /// <summary>Used to time the flare.</summary>
    Flare,

    /// <summary>Used for the flare and the glide slope.</summary>
    GlideSlope
}

/// <summary>
/// The landing behaviour a plan asks of the vehicle (PX4 FW_LND_USETER and FW_LND_ABORT). PX4's own default aborts a
/// landing whenever the rangefinder loses the ground for a second, then climbs out and loiters; the plan's default does
/// not abort and lands on the last good estimate.
/// </summary>
public sealed record LandingSettings
{
    /// <summary>How the rangefinder is used.</summary>
    public LandingTerrainUse TerrainUse { get; init; } = LandingTerrainUse.Flare;

    /// <summary>Abort when the rangefinder never finds the ground during the landing (FW_LND_ABORT bit 0).</summary>
    public bool AbortIfTerrainNotFound { get; init; }

    /// <summary>Abort when the rangefinder loses the ground for more than a second (FW_LND_ABORT bit 1).</summary>
    public bool AbortIfTerrainLost { get; init; }
}

/// <summary>A flight plan: settings plus an ordered item list. Edit it with <see cref="MissionEditor"/>.</summary>
public sealed record Mission
{
    /// <summary>An empty mission with default settings.</summary>
    public static Mission Empty { get; } = new();

    /// <summary>Mission-wide settings.</summary>
    public MissionSettings Settings { get; init; } = new();

    /// <summary>Items in flight order.</summary>
    public ImmutableList<MissionItem> Items { get; init; } = [];

    /// <summary>Index of the item with this id, or -1.</summary>
    public int IndexOf(Guid id) => Items.FindIndex(item => item.Id == id);
}

/// <summary>
/// Optional overrides when creating an item. Anything left null falls back to the type's default, or to
/// <see cref="MissionSettings"/> where one applies.
/// </summary>
public sealed record MissionItemOptions
{
    /// <summary>Altitude above home.</summary>
    public double? AltitudeMeters { get; init; }

    /// <summary>Airspeed for a speed change.</summary>
    public double? SpeedMps { get; init; }

    /// <summary>Approach heading of a landing pattern.</summary>
    public double? HeadingDegrees { get; init; }

    /// <summary>Orbit radius.</summary>
    public double? LoiterRadiusMeters { get; init; }

    /// <summary>Loiter duration.</summary>
    public double? LoiterTimeSeconds { get; init; }

    /// <summary>Delay duration.</summary>
    public double? DelaySeconds { get; init; }

    /// <summary>Approach length of a landing pattern.</summary>
    public double? DistanceMeters { get; init; }

    /// <summary>Target of a jump.</summary>
    public Guid? JumpTargetId { get; init; }

    /// <summary>Repeats of a jump.</summary>
    public double? JumpRepeatCount { get; init; }

    /// <summary>Distance between camera triggers.</summary>
    public double? CameraTriggerDistanceMeters { get; init; }
}
