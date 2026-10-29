namespace Ketras.Vehicles;

/// <summary>Where the current draw of an energy budget came from.</summary>
public enum CurrentSource
{
    /// <summary>The cruise current the operator entered.</summary>
    Entered,

    /// <summary>The average measured since arming.</summary>
    Measured
}

/// <summary>Whether a flight fits in the battery, above the reserve the autopilot keeps.</summary>
/// <param name="UsableMah">Charge left above the reserve.</param>
/// <param name="NeededMah">Charge the remaining flight takes.</param>
/// <param name="CurrentAmps">The draw used.</param>
/// <param name="Source">Where the draw came from.</param>
public sealed record EnergyBudget(double UsableMah, double NeededMah, double CurrentAmps, CurrentSource Source)
{
    /// <summary>Charge left over at the end, above the reserve; negative when the flight does not fit.</summary>
    public double MarginMah => UsableMah - NeededMah;

    /// <summary>The margin as a share of the usable charge, %; −100 or less when nothing is usable.</summary>
    public double MarginPercent => UsableMah > 0 ? MarginMah / UsableMah * 100 : -100;

    /// <summary>Time the usable charge lasts at the draw, seconds.</summary>
    public double EnduranceSeconds => CurrentAmps > 0 ? Math.Max(0, UsableMah) * 3.6 / CurrentAmps : double.PositiveInfinity;
}

/// <summary>
/// Calculator: the energy budget of a flight. The usable charge is the capacity times the remaining share above the
/// autopilot's low-battery threshold (PX4 BAT_LOW_THR, where its low-battery action starts), so the margin is what is
/// left before that action; the need is the current times the time (1 A for 1 h = 1000 mAh).
/// </summary>
public static class EnergyCalculator
{
    /// <summary>A margin below this share of the usable charge is a warning, %.</summary>
    public const double WarningMarginPercent = 20;

    /// <summary>The budget; null without a capacity or a draw.</summary>
    /// <param name="capacityMah">Battery capacity.</param>
    /// <param name="remainingPercent">Charge left, 0…100.</param>
    /// <param name="reserveFraction">The low-battery threshold, 0…1.</param>
    /// <param name="seconds">The flight time still to fly.</param>
    /// <param name="currentAmps">The draw.</param>
    /// <param name="source">Where the draw came from.</param>
    public static EnergyBudget? Budget(double capacityMah, double remainingPercent, double reserveFraction, double seconds, double currentAmps, CurrentSource source)
    {
        if (capacityMah <= 0 || currentAmps <= 0 || !double.IsFinite(seconds))
        {
            return null;
        }

        var usable = capacityMah * ((Math.Clamp(remainingPercent, 0, 100) / 100) - Math.Clamp(reserveFraction, 0, 1));
        return new EnergyBudget(usable, currentAmps * seconds / 3.6, currentAmps, source);
    }

    /// <summary>
    /// The average draw since arming from the charge used then and now; null before a minute of flight or without the
    /// charge counter.
    /// </summary>
    public static double? AverageCurrent(double? consumedAtArmMah, double? consumedNowMah, TimeSpan sinceArmed) =>
        consumedAtArmMah is { } start && consumedNowMah is { } now && sinceArmed >= TimeSpan.FromMinutes(1) && now > start
            ? (now - start) * 3.6 / sinceArmed.TotalSeconds
            : null;
}
