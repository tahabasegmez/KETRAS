using System.Globalization;
using Ketras.Mavlink.Messages;

namespace Ketras.Mavlink.Autopilots;

/// <summary>
/// Fallback for autopilots without a profile: no named modes, C-cast parameter encoding. Enough to
/// read parameters and telemetry; mode commands need a real profile.
/// </summary>
internal sealed class GenericProfile : IAutopilotProfile
{
    /// <inheritdoc />
    public string Name => "generic";

    /// <inheritdoc />
    public IReadOnlyList<string> ModeNames { get; } = [];

    /// <inheritdoc />
    public bool MissionHomeAtSequenceZero => false;

    /// <inheritdoc />
    public string ModeName(byte baseMode, uint customMode) =>
        string.Create(CultureInfo.InvariantCulture, $"CUSTOM({customMode})");

    /// <inheritdoc />
    public bool TryGetSetModeParams(string modeName, out float param1, out float param2, out float param3)
    {
        param1 = param2 = param3 = 0;
        return false;
    }

    /// <inheritdoc />
    public float EncodeParameter(ParameterValue value) => ParameterEncoding.EncodeCast(value);

    /// <inheritdoc />
    public ParameterValue DecodeParameter(float raw, MavParamType type) => ParameterEncoding.DecodeCast(raw, type);

    /// <inheritdoc />
    public string? DecodeGitHash(ReadOnlySpan<byte> flightCustomVersion)
    {
        // ASCII, as ArduPilot sends it; anything unprintable means there is no usable hash.
        var end = flightCustomVersion.IndexOf((byte)0);
        var text = flightCustomVersion[..(end < 0 ? flightCustomVersion.Length : end)];
        return text.Length > 0 && text.IndexOfAnyExceptInRange((byte)0x21, (byte)0x7E) < 0
            ? System.Text.Encoding.ASCII.GetString(text)
            : null;
    }
}
