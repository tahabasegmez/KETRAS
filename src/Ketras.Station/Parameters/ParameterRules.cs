using Ketras.Mavlink.Messages;
using Ketras.Station.Vehicles;

namespace Ketras.Station.Parameters;

/// <summary>Why a value cannot be stored.</summary>
public enum ValueProblem
{
    /// <summary>It can.</summary>
    None,

    /// <summary>An integer parameter given a fraction.</summary>
    NotWhole,

    /// <summary>Below the metadata minimum.</summary>
    BelowMinimum,

    /// <summary>Above the metadata maximum.</summary>
    AboveMaximum
}

/// <summary>Why the vehicle's parameters cannot be edited now.</summary>
public enum EditBlock
{
    /// <summary>They can.</summary>
    None,

    /// <summary>No vehicle.</summary>
    NoVehicle,

    /// <summary>The link is lost; values are from the last read.</summary>
    LinkLost,

    /// <summary>The vehicle is armed.</summary>
    Armed,

    /// <summary>The full list has not been read (or is being read again).</summary>
    Loading
}

/// <summary>Pure rules for parameter values: range and type checks, equality in the parameter's own type, and when editing is allowed.</summary>
public static class ParameterRules
{
    /// <summary>The type holds whole numbers.</summary>
    public static bool IsInteger(MavParamType type) => type is not (MavParamType.Real32 or MavParamType.Real64);

    /// <summary>Whether <paramref name="value"/> can be stored in a parameter of <paramref name="type"/>.</summary>
    public static ValueProblem Check(double value, MavParamType type, ParameterMetadata? metadata) =>
        IsInteger(type) && Math.Abs(value - Math.Round(value)) > 1e-9 ? ValueProblem.NotWhole
        : metadata?.Min is { } min && value < min - Tolerance(min) ? ValueProblem.BelowMinimum
        : metadata?.Max is { } max && value > max + Tolerance(max) ? ValueProblem.AboveMaximum
        : ValueProblem.None;

    /// <summary>Two values are the same once stored in <paramref name="type"/> (a float parameter keeps 32 bits).</summary>
    public static bool Same(double a, double b, MavParamType type) => type switch
    {
        MavParamType.Real32 => (float)a == (float)b,
        MavParamType.Real64 => a.Equals(b),
        _ => Math.Round(a) == Math.Round(b)
    };

    /// <summary>The value differs from the firmware default; false without a default.</summary>
    public static bool IsModified(double value, ParameterMetadata? metadata) =>
        metadata?.Default is { } defaultValue && !Same(value, defaultValue, metadata.Type);

    /// <summary>Whether the vehicle's parameters can be edited (and calibrations run).</summary>
    public static EditBlock WhyNotEditable(Vehicle? vehicle) =>
        vehicle is null ? EditBlock.NoVehicle
        : !vehicle.IsConnected ? EditBlock.LinkLost
        : vehicle.Snapshot.IsArmed ? EditBlock.Armed
        : !vehicle.Parameters.IsLoaded || vehicle.Parameters.IsRefreshing ? EditBlock.Loading
        : EditBlock.None;

    private static double Tolerance(double limit) => Math.Max(Math.Abs(limit), 1) * 1e-6;
}
