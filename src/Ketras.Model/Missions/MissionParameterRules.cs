using Ketras.Model.Geo;
using Ketras.Model.Vehicles;

namespace Ketras.Model.Missions;

/// <summary>Why a value has a span narrower than its parameter's own limits.</summary>
public enum RangeReason
{
    /// <summary>Only the parameter's own limits apply.</summary>
    None,

    /// <summary>An orbit tighter than the aircraft can turn; the value is the airspeed (m/s) the turn is flown at.</summary>
    TurnRadius,

    /// <summary>The vehicle's airspeed limits.</summary>
    Airspeed,

    /// <summary>The steepest landing glide slope; the value is that slope in degrees.</summary>
    GlideSlope,

    /// <summary>What the climb and sink rates reach over the leg from an earlier item; the value is that item's number.</summary>
    ClimbFromPrevious,

    /// <summary>The terrain under the item; the value is the ground height in the plan's reference.</summary>
    Ground
}

/// <summary>The values a parameter of one item may take.</summary>
/// <param name="HardMinimum">Smallest value accepted at all.</param>
/// <param name="HardMaximum">Largest value accepted at all.</param>
/// <param name="SoftMinimum">Smallest value the vehicle can fly as planned; null without such a limit.</param>
/// <param name="SoftMaximum">Largest value the vehicle can fly as planned; null without such a limit.</param>
/// <param name="Reason">Where the soft limits come from.</param>
/// <param name="ReasonValue">The figure the reason names (see <see cref="RangeReason"/>).</param>
public sealed record ParameterRange(double HardMinimum, double HardMaximum, double? SoftMinimum, double? SoftMaximum, RangeReason Reason, double ReasonValue)
{
    /// <summary>True when <paramref name="value"/> lies outside the soft limits.</summary>
    public bool IsOutsideSoft(double value) => value < SoftMinimum || value > SoftMaximum;
}

/// <summary>
/// Pure rules: the span each parameter of an item may take, from its own limits narrowed by the vehicle's (turn
/// radius, airspeed, glide slope, climb and sink over the leg) and the terrain. The same figures
/// <see cref="MissionRules.Validate"/> checks a plan against, shown before a value is entered.
/// </summary>
public static class MissionParameterRules
{
    /// <summary>The span of <paramref name="parameter"/> for <paramref name="item"/> in <paramref name="mission"/>.</summary>
    public static ParameterRange Range(Mission mission, MissionItem item, MissionParameter parameter, VehicleLimits limits, Func<GeoPoint, double?>? groundAt)
    {
        var hard = new ParameterRange(parameter.Minimum, parameter.Maximum, null, null, RangeReason.None, 0);
        var settings = mission.Settings;
        var index = mission.IndexOf(item.Id);
        switch (parameter.Key)
        {
            case "radius" or "orbit-radius":
            {
                var speed = Math.Clamp(SpeedAt(mission, index), limits.MinAirspeedMps, limits.MaxAirspeedMps);
                return hard with { SoftMinimum = MissionRules.MinimumTurnRadiusMeters(speed, limits.MaxBankDegrees), Reason = RangeReason.TurnRadius, ReasonValue = speed };
            }

            case "airspeed":
                return hard with { SoftMinimum = limits.MinAirspeedMps, SoftMaximum = limits.MaxAirspeedMps, Reason = RangeReason.Airspeed };

            case "approach-length" when MissionRules.LandingDrop(settings, item, groundAt) is { } drop and > 0:
                return hard with
                {
                    SoftMinimum = drop / Math.Tan(limits.MaxLandingSlopeDegrees * Math.PI / 180), Reason = RangeReason.GlideSlope,
                    ReasonValue = limits.MaxLandingSlopeDegrees
                };

            case "altitude" or "approach-altitude" when Reach(mission, index, item, limits, groundAt) is { } reach:
                return hard with { SoftMinimum = reach.Low, SoftMaximum = reach.High, Reason = RangeReason.ClimbFromPrevious, ReasonValue = reach.From };

            case "touchdown-altitude" when item.Location is { } touchdown && groundAt?.Invoke(touchdown) is { } ground
                                           && MissionAltitudes.FromAmsl(settings, settings.AltitudeReference, ground, touchdown, groundAt) is { } level:
                return hard with { SoftMinimum = level, SoftMaximum = level, Reason = RangeReason.Ground, ReasonValue = level };

            default:
                return hard;
        }
    }

    /// <summary>The airspeed in effect at item <paramref name="index"/>: the last speed change before it, else the cruise speed.</summary>
    public static double SpeedAt(Mission mission, int index)
    {
        var speed = mission.Settings.CruiseSpeedMps;
        for (var i = 0; i < index && i < mission.Items.Count; i++)
        {
            if (mission.Items[i] is { Type: MissionItemType.ChangeSpeed, Param2: > 0 } change)
            {
                speed = change.Param2;
            }
        }

        return speed;
    }

    /// <summary>The located item before <paramref name="index"/>, with its index; null when there is none.</summary>
    public static (int Index, MissionItem Item)? PreviousLocated(Mission mission, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (MissionAltitudes.PositionOf(mission.Items[i]) is not null)
            {
                return (i, mission.Items[i]);
            }
        }

        return null;
    }

    /// <summary>
    /// The altitudes (in the plan's reference) reachable at the item from the previous located one at the vehicle's
    /// climb and sink rates and the airspeed in effect; null without a previous item or known heights.
    /// </summary>
    private static (double Low, double High, int From)? Reach(Mission mission, int index, MissionItem item, VehicleLimits limits, Func<GeoPoint, double?>? groundAt)
    {
        var settings = mission.Settings;
        if (index <= 0 || MissionAltitudes.PositionOf(item) is not { } here || PreviousLocated(mission, index) is not { } previous
            || MissionAltitudes.PositionOf(previous.Item) is not { } there || MissionAltitudes.AmslOf(settings, previous.Item, groundAt) is not { } start)
        {
            return null;
        }

        var seconds = GeoMath.DistanceMeters(there, here) / Math.Max(SpeedAt(mission, index), 1);
        var low = MissionAltitudes.FromAmsl(settings, settings.AltitudeReference, start - (limits.MaxSinkRateMps * seconds), here, groundAt);
        var high = MissionAltitudes.FromAmsl(settings, settings.AltitudeReference, start + (limits.MaxClimbRateMps * seconds), here, groundAt);
        return low is { } l && high is { } h ? (l, h, previous.Index + 1) : null;
    }
}
