using Ketras.Mavlink.Messages;

namespace Ketras.Mavlink.Autopilots;

/// <summary>A parameter value with its MAVLink type. Integer types hold whole numbers in <see cref="Value"/>.</summary>
public readonly record struct ParameterValue(MavParamType Type, double Value)
{
    /// <summary>True for every integer MAV_PARAM_TYPE.</summary>
    public bool IsInteger => Type is not (MavParamType.Real32 or MavParamType.Real64);
}

/// <summary>
/// Strategy: everything that differs between autopilot firmwares: flight mode names, how parameter
/// values are packed into PARAM_VALUE/PARAM_SET, and mission sequence rules. The connection and the
/// protocol clients stay autopilot-neutral and ask the profile.
/// </summary>
public interface IAutopilotProfile
{
    /// <summary>Profile name, e.g. "px4".</summary>
    string Name { get; }

    /// <summary>Flight modes this autopilot can be commanded into, in its own naming.</summary>
    IReadOnlyList<string> ModeNames { get; }

    /// <summary>Mode name for a HEARTBEAT's base and custom mode.</summary>
    string ModeName(byte baseMode, uint customMode);

    /// <summary>MAV_CMD_DO_SET_MODE params 1-3 for a mode name; false if the name is unknown.</summary>
    bool TryGetSetModeParams(string modeName, out float param1, out float param2, out float param3);

    /// <summary>Packs a value into the float field of PARAM_SET.</summary>
    float EncodeParameter(ParameterValue value);

    /// <summary>Unpacks the float field of PARAM_VALUE.</summary>
    ParameterValue DecodeParameter(float raw, MavParamType type);

    /// <summary>True when mission sequence 0 is the home position rather than the first item.</summary>
    bool MissionHomeAtSequenceZero { get; }

    /// <summary>Source revision from AUTOPILOT_VERSION.flight_custom_version (8 bytes), or null.</summary>
    string? DecodeGitHash(ReadOnlySpan<byte> flightCustomVersion);
}
