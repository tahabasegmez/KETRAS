using Ketras.Mavlink.Messages;

namespace Ketras.Parameters;

/// <summary>One named value of an enum parameter.</summary>
/// <param name="Value">The stored value.</param>
/// <param name="Description">Its name, as the firmware describes it.</param>
public sealed record EnumOption(long Value, string Description);

/// <summary>One bit of a bitmask parameter.</summary>
/// <param name="Index">The bit, from 0.</param>
/// <param name="Description">What it switches.</param>
public sealed record BitmaskBit(int Index, string Description);

/// <summary>
/// What the firmware's parameter metadata (PX4 <c>parameters.json</c>, MAVLink component metadata type 1) says about
/// one parameter. It only describes: which parameters exist is decided by what the vehicle reports.
/// </summary>
public sealed record ParameterMetadata
{
    /// <summary>The parameter name.</summary>
    public required string Name { get; init; }

    /// <summary>Its type.</summary>
    public required MavParamType Type { get; init; }

    /// <summary>The firmware's group, e.g. "Battery Calibration".</summary>
    public string Group { get; init; } = string.Empty;

    /// <summary>"Standard", "System" or "Developer".</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>One line.</summary>
    public string ShortDescription { get; init; } = string.Empty;

    /// <summary>The full explanation; may be empty.</summary>
    public string LongDescription { get; init; } = string.Empty;

    /// <summary>Unit symbol, e.g. "m/s"; empty when none.</summary>
    public string Units { get; init; } = string.Empty;

    /// <summary>Smallest valid value.</summary>
    public double? Min { get; init; }

    /// <summary>Largest valid value.</summary>
    public double? Max { get; init; }

    /// <summary>Suggested step.</summary>
    public double? Increment { get; init; }

    /// <summary>Decimals to show.</summary>
    public int? DecimalPlaces { get; init; }

    /// <summary>The firmware default.</summary>
    public double? Default { get; init; }

    /// <summary>Named values; empty when the parameter is not an enum.</summary>
    public IReadOnlyList<EnumOption> Options { get; init; } = [];

    /// <summary>Bits; empty when the parameter is not a bitmask.</summary>
    public IReadOnlyList<BitmaskBit> Bits { get; init; } = [];

    /// <summary>A change takes effect after a reboot.</summary>
    public bool RebootRequired { get; init; }

    /// <summary>The vehicle changes it itself (not a setting).</summary>
    public bool Volatile { get; init; }

    /// <summary>It has named values.</summary>
    public bool IsEnum => Options.Count > 0;

    /// <summary>It is a set of bits.</summary>
    public bool IsBitmask => Bits.Count > 0;
}
