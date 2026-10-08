namespace Ketras.Station.Setup;

/// <summary>
/// Pure RC and power arithmetic: the mode slot a PWM selects and switch states and the battery factor calculators.
/// </summary>
public static class RcRules
{
    /// <summary>Mode slots on the mode channel.</summary>
    public const int SlotCount = 6;

    /// <summary>The 1-based slot a raw PWM on the mode channel selects, using that channel's calibration.</summary>
    public static int ActiveSlot(int pwm, double min, double max, double trim, double reverse)
    {
        var calibrated = pwm > trim && max > trim ? (pwm - trim) / (max - trim)
            : pwm < trim && trim > min ? (pwm - trim) / (trim - min)
            : 0;
        calibrated *= reverse < 0 ? -1 : 1;

        const double slotWidthHalf = 2.0 / SlotCount / 2.0;
        const double slotMin = -1.0 - 0.05;
        const double slotMax = 1.0 + 0.05;
        var slot = (int)((((calibrated - slotMin) * SlotCount) + slotWidthHalf) / (slotMax - slotMin) + (1.0 / SlotCount));
        return Math.Clamp(slot, 0, SlotCount - 1) + 1;
    }

    /// <summary>Whether a switch is on for its <c>RC_*_TH</c> threshold (0..1 of the range; negative inverts it).</summary>
    public static bool SwitchOn(int pwm, double threshold)
    {
        var level = 1000 + 1000 * Math.Abs(threshold);
        return threshold >= 0 ? pwm > level : pwm < level;
    }

    /// <summary>A new divider or amps-per-volt factor: measured × current factor ÷ the vehicle's reading; null when it cannot be computed.</summary>
    public static double? BatteryFactor(double measured, double currentFactor, double reading)
    {
        var factor = measured * currentFactor / reading;
        return measured > 0 && reading > 0 && double.IsFinite(factor) && factor > 0 ? factor : null;
    }
}
