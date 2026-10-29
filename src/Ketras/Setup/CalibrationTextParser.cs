namespace Ketras.Setup;

/// <summary>What a calibration status text says.</summary>
public enum CalibrationEventKind
{
    /// <summary>"calibration started: 2 accel".</summary>
    Started,

    /// <summary>"progress &lt;40&gt;".</summary>
    Progress,

    /// <summary>"down orientation detected".</summary>
    OrientationDetected,

    /// <summary>"down side done, rotate to a different side".</summary>
    SideDone,

    /// <summary>"down side already completed".</summary>
    SideAlreadyDone,

    /// <summary>"calibration done: accel".</summary>
    Done,

    /// <summary>"calibration cancelled".</summary>
    Cancelled,

    /// <summary>"calibration failed: …".</summary>
    Failed,

    /// <summary>Any other <c>[cal]</c> line.</summary>
    Info
}

/// <summary>One parsed calibration status text.</summary>
/// <param name="Kind">What it says.</param>
/// <param name="Text">The text after the prefix (for Failed, the reason).</param>
/// <param name="Sensor">For Started: "accel", "mag", "gyro", "level", "airspeed".</param>
/// <param name="Percent">For Progress.</param>
/// <param name="Side">For the side events: "down", "up", "left", "right", "front", "back".</param>
public sealed record CalibrationEvent(CalibrationEventKind Kind, string Text, string? Sensor = null, int? Percent = null, string? Side = null);

/// <summary>
/// Parses PX4's calibration status texts (<c>src/modules/commander/calibration_messages.h</c>, protocol version 2).
/// </summary>
public static class CalibrationTextParser
{
    /// <summary>The prefix of every calibration text.</summary>
    public const string Prefix = "[cal] ";

    /// <summary>The sides PX4 names, in display order.</summary>
    public static IReadOnlyList<string> Sides { get; } = ["down", "up", "left", "right", "front", "back"];

    /// <summary>The event in <paramref name="raw"/>; null when it is not a calibration text.</summary>
    public static CalibrationEvent? Parse(string raw)
    {
        var text = raw.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal);
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var body = text[Prefix.Length..].Trim();
        if (body.StartsWith("progress <", StringComparison.Ordinal))
        {
            var start = body.IndexOf('<', StringComparison.Ordinal) + 1;
            var end = body.IndexOf('>', start);
            return end > start && int.TryParse(body[start..end], System.Globalization.CultureInfo.InvariantCulture, out var percent)
                ? new CalibrationEvent(CalibrationEventKind.Progress, body, Percent: Math.Clamp(percent, 0, 100))
                : new CalibrationEvent(CalibrationEventKind.Info, body);
        }

        if (body.StartsWith("calibration started: ", StringComparison.Ordinal))
        {
            var parts = body["calibration started: ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new CalibrationEvent(CalibrationEventKind.Started, body, Sensor: parts.LastOrDefault());
        }

        return body switch
        {
            _ when body.EndsWith("orientation detected", StringComparison.Ordinal) => new(CalibrationEventKind.OrientationDetected, body, Side: FirstWord(body)),
            _ when body.EndsWith("side done, rotate to a different side", StringComparison.Ordinal) => new(CalibrationEventKind.SideDone, body, Side: FirstWord(body)),
            _ when body.EndsWith("side already completed", StringComparison.Ordinal) => new(CalibrationEventKind.SideAlreadyDone, body, Side: FirstWord(body)),
            _ when body.StartsWith("calibration done:", StringComparison.Ordinal) => new(CalibrationEventKind.Done, body),
            _ when body.StartsWith("calibration cancelled", StringComparison.Ordinal) => new(CalibrationEventKind.Cancelled, body),
            _ when body.StartsWith("calibration failed", StringComparison.Ordinal) =>
                new(CalibrationEventKind.Failed, body.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0 ? body[(colon + 1)..].Trim() : body),
            _ => new(CalibrationEventKind.Info, body)
        };
    }

    /// <summary>The sides a compass calibration asks for, from the <c>SENS_MAG_SIDES</c> bitmask (bit 0 back … bit 5 down); all without it.</summary>
    public static IReadOnlyList<string> CompassSides(double? bitmask)
    {
        var bits = (long)(bitmask ?? 63);
        string[] byBit = ["back", "front", "left", "right", "up", "down"];
        return [.. Sides.Where(side => (bits & (1L << Array.IndexOf(byBit, side))) != 0)];
    }

    private static string FirstWord(string body) => body.Split(' ', 2)[0];
}
