namespace Ketras.Planning.Missions;

/// <summary>Which slot of a <see cref="MissionItem"/> an editable value lives in.</summary>
public enum MissionItemField
{
    /// <summary>MAVLink parameter 1.</summary>
    Param1,

    /// <summary>MAVLink parameter 2.</summary>
    Param2,

    /// <summary>MAVLink parameter 3.</summary>
    Param3,

    /// <summary>MAVLink parameter 4.</summary>
    Param4,

    /// <summary>Altitude above home.</summary>
    Altitude,

    /// <summary>Magnitude of the orbit radius in Param2; the sign is kept.</summary>
    OrbitRadius,

    /// <summary>Sign of the orbit radius in Param2: 1 clockwise, -1 counter-clockwise.</summary>
    OrbitDirection
}

/// <summary>What kind of quantity a value is, so a screen picks its unit and format.</summary>
public enum Quantity
{
    /// <summary>A plain number.</summary>
    Number,

    /// <summary>A height.</summary>
    Altitude,

    /// <summary>A horizontal distance.</summary>
    Distance,

    /// <summary>A speed.</summary>
    Speed,

    /// <summary>A duration.</summary>
    Duration,

    /// <summary>An angle or heading in degrees.</summary>
    Angle,

    /// <summary>One of <see cref="MissionParameter.Choices"/>.</summary>
    Choice
}

/// <summary>One choice of a choice parameter.</summary>
/// <param name="Key">Stable key for the label, e.g. "cw".</param>
/// <param name="Value">Stored value.</param>
public sealed record ParameterChoice(string Key, double Value);

/// <summary>
/// One editable value of an item type. <see cref="Key"/> names it for labels and translations (e.g. "approach-length");
/// the model holds no display text.
/// </summary>
/// <param name="Field">Where the value lives.</param>
/// <param name="Key">Stable key.</param>
/// <param name="Quantity">Kind of value.</param>
/// <param name="Minimum">Smallest accepted value.</param>
/// <param name="Maximum">Largest accepted value.</param>
/// <param name="Choices">The values a choice parameter can take.</param>
public sealed record MissionParameter(
    MissionItemField Field,
    string Key,
    Quantity Quantity,
    double Minimum = double.MinValue,
    double Maximum = double.MaxValue,
    IReadOnlyList<ParameterChoice>? Choices = null);

/// <summary>The editable values of each item type, and how to read and write them.</summary>
public static class MissionItemParameters
{
    private static MissionParameter Altitude(string key = "altitude") => new(MissionItemField.Altitude, key, Quantity.Altitude, 0, 20000);

    private static readonly MissionParameter Direction = new(MissionItemField.OrbitDirection, "orbit-direction", Quantity.Choice,
        Choices: [new ParameterChoice("cw", 1), new ParameterChoice("ccw", -1)]);

    private static readonly Dictionary<MissionItemType, IReadOnlyList<MissionParameter>> Table = new()
    {
        [MissionItemType.Takeoff] = [Altitude()],
        [MissionItemType.Waypoint] = [Altitude()],
        [MissionItemType.LoiterUnlimited] = [Altitude(), new(MissionItemField.Param3, "radius", Quantity.Distance, 10, 5000)],
        [MissionItemType.LoiterTime] =
        [
            Altitude(),
            new(MissionItemField.Param1, "loiter-time", Quantity.Duration, 0, 3600),
            new(MissionItemField.Param3, "radius", Quantity.Distance, 10, 5000)
        ],
        [MissionItemType.LoiterToAltitude] = [Altitude(), new(MissionItemField.Param2, "radius", Quantity.Distance, 10, 5000)],
        [MissionItemType.RegionOfInterest] = [Altitude()],
        [MissionItemType.ChangeSpeed] = [new(MissionItemField.Param2, "airspeed", Quantity.Speed, 0, 120)],
        [MissionItemType.TakePhoto] = [],
        [MissionItemType.StartVideo] = [],
        [MissionItemType.StopVideo] = [],
        [MissionItemType.CameraTriggerDistance] = [new(MissionItemField.Param1, "trigger-distance", Quantity.Distance, 0, 10000)],
        [MissionItemType.DelaySeconds] = [new(MissionItemField.Param1, "delay", Quantity.Duration, 0, 3600)],
        [MissionItemType.DoJump] = [new(MissionItemField.Param2, "repeats", Quantity.Number, 1, 100)],
        [MissionItemType.LandingPattern] =
        [
            Altitude("approach-altitude"),
            new(MissionItemField.Param1, "touchdown-altitude", Quantity.Altitude, -500, 20000),
            new(MissionItemField.OrbitRadius, "orbit-radius", Quantity.Distance, 10, 5000),
            Direction,
            new(MissionItemField.Param3, "approach-length", Quantity.Distance, 50, 20000),
            new(MissionItemField.Param4, "approach-heading", Quantity.Angle, 0, 360)
        ],
        [MissionItemType.Land] = [Altitude()],
        [MissionItemType.ReturnToLaunch] = []
    };

    /// <summary>The editable values of an item type, in display order.</summary>
    public static IReadOnlyList<MissionParameter> For(MissionItemType type) => Table[type];

    /// <summary>The value in <paramref name="field"/>.</summary>
    public static double Read(MissionItem item, MissionItemField field) => field switch
    {
        MissionItemField.Param1 => item.Param1,
        MissionItemField.Param2 => item.Param2,
        MissionItemField.Param3 => item.Param3,
        MissionItemField.Param4 => item.Param4,
        MissionItemField.OrbitRadius => Math.Abs(item.Param2),
        MissionItemField.OrbitDirection => item.Param2 < 0 ? -1 : 1,
        _ => item.AltitudeMeters
    };

    /// <summary>The item with <paramref name="value"/> written to <paramref name="field"/>.</summary>
    public static MissionItem Write(MissionItem item, MissionItemField field, double value) => field switch
    {
        MissionItemField.Param1 => item with { Param1 = value },
        MissionItemField.Param2 => item with { Param2 = value },
        MissionItemField.Param3 => item with { Param3 = value },
        MissionItemField.Param4 => item with { Param4 = value },
        MissionItemField.OrbitRadius => item with { Param2 = item.Param2 < 0 ? -Math.Abs(value) : Math.Abs(value) },
        MissionItemField.OrbitDirection => item with { Param2 = Math.Abs(item.Param2) * (value < 0 ? -1 : 1) },
        _ => item with { AltitudeMeters = value }
    };
}
