namespace Ketras.Controls.Instruments;

/// <summary>
/// Calculator: where a value sits on a dial. Angles are degrees clockwise from twelve o'clock; values outside the
/// scale stop at its ends.
/// </summary>
internal static class DialScale
{
    /// <summary>The angle of <paramref name="value"/> on a scale from <paramref name="minimum"/> (at <paramref name="startDegrees"/>) sweeping <paramref name="sweepDegrees"/>.</summary>
    public static double AngleFor(double value, double minimum, double maximum, double startDegrees, double sweepDegrees)
    {
        if (maximum <= minimum)
        {
            return startDegrees;
        }

        var fraction = Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
        return startDegrees + (fraction * sweepDegrees);
    }

    /// <summary>The share of the range <paramref name="value"/> fills, 0…1.</summary>
    public static double Fraction(double value, double minimum, double maximum) =>
        maximum <= minimum ? 0 : Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);

    /// <summary>Tick values from <paramref name="minimum"/> to <paramref name="maximum"/> every <paramref name="step"/>.</summary>
    public static IReadOnlyList<double> Ticks(double minimum, double maximum, double step)
    {
        var ticks = new List<double>();
        if (step <= 0 || maximum < minimum)
        {
            return ticks;
        }

        var count = (int)Math.Floor(((maximum - minimum) / step) + 1e-9);
        for (var i = 0; i <= count; i++)
        {
            ticks.Add(minimum + (i * step));
        }

        return ticks;
    }

    /// <summary>A step of 1, 2 or 5 × 10ⁿ that gives about <paramref name="divisions"/> divisions over <paramref name="span"/>.</summary>
    public static double NiceStep(double span, int divisions)
    {
        if (span <= 0 || divisions <= 0)
        {
            return 1;
        }

        var raw = span / divisions;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var normalized = raw / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    /// <summary>A point at <paramref name="radius"/> from <paramref name="centerX"/>, <paramref name="centerY"/> in direction <paramref name="degrees"/>.</summary>
    public static (double X, double Y) PointAt(double centerX, double centerY, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return (centerX + (radius * Math.Sin(radians)), centerY - (radius * Math.Cos(radians)));
    }
}
