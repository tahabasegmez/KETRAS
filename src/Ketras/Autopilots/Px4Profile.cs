using Ketras.Mavlink.Messages;

namespace Ketras.Autopilots;

/// <summary>
/// PX4: custom mode = main mode in bits 16-23, sub mode in bits 24-31; bytewise parameter encoding;
/// no home at mission sequence 0. Mode names follow the common "MAIN.SUB" convention. Sources in docs/reference.
/// </summary>
internal sealed class Px4Profile : IAutopilotProfile
{
    private const byte CustomModeEnabled = 1;

    private static readonly (string Name, byte Main, byte Sub)[] Modes =
    [
        ("MANUAL", 1, 0),
        ("ALTCTL", 2, 0),
        ("POSCTL", 3, 0),
        ("POSCTL.ORBIT", 3, 1),
        ("POSCTL.SLOW", 3, 2),
        ("AUTO.READY", 4, 1),
        ("AUTO.TAKEOFF", 4, 2),
        ("AUTO.LOITER", 4, 3),
        ("AUTO.MISSION", 4, 4),
        ("AUTO.RTL", 4, 5),
        ("AUTO.LAND", 4, 6),
        ("AUTO.FOLLOW_TARGET", 4, 8),
        ("AUTO.PRECLAND", 4, 9),
        ("AUTO.VTOL_TAKEOFF", 4, 10),
        ("AUTO.DESCEND", 4, 20),
        ("ACRO", 5, 0),
        ("OFFBOARD", 6, 0),
        ("STABILIZED", 7, 0),
        ("TERMINATION", 10, 0),
        ("ALTITUDE_CRUISE", 11, 0),
    ];

    /// <inheritdoc />
    public string Name => "px4";

    /// <inheritdoc />
    public IReadOnlyList<string> ModeNames { get; } = Modes.Select(m => m.Name).ToList();

    /// <inheritdoc />
    public bool MissionHomeAtSequenceZero => false;

    /// <inheritdoc />
    public string ModeName(byte baseMode, uint customMode)
    {
        var main = (byte)((customMode >> 16) & 0xFF);
        var sub = (byte)((customMode >> 24) & 0xFF);

        foreach (var mode in Modes)
        {
            // Only AUTO and POSCTL carry a meaningful sub mode.
            if (mode.Main == main && (mode.Sub == sub || main is not (3 or 4)))
            {
                return mode.Name;
            }
        }

        return $"CUSTOM({main}.{sub})";
    }

    /// <inheritdoc />
    public bool TryGetSetModeParams(string modeName, out float param1, out float param2, out float param3)
    {
        foreach (var mode in Modes)
        {
            if (string.Equals(mode.Name, modeName, StringComparison.OrdinalIgnoreCase))
            {
                param1 = CustomModeEnabled;
                param2 = mode.Main;
                param3 = mode.Sub;
                return true;
            }
        }

        param1 = param2 = param3 = 0;
        return false;
    }

    /// <inheritdoc />
    public float EncodeParameter(ParameterValue value) => ParameterEncoding.EncodeBytewise(value);

    /// <inheritdoc />
    public ParameterValue DecodeParameter(float raw, MavParamType type) => ParameterEncoding.DecodeBytewise(raw, type);

    /// <inheritdoc />
    public string? DecodeGitHash(ReadOnlySpan<byte> flightCustomVersion)
    {
        // PX4 stores the first 16 hex digits of the git hash as binary, bytes in reverse order.
        if (flightCustomVersion.Length < 8 || flightCustomVersion[..8].IndexOfAnyExcept((byte)0) < 0)
        {
            return null;
        }

        var hash = new System.Text.StringBuilder(16);
        for (var i = 7; i >= 0; i--)
        {
            hash.Append(flightCustomVersion[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return hash.ToString();
    }
}
