using System.Globalization;
using Ketras.Model.Missions;
using Ketras.Resources;

namespace Ketras.Planning.Missions;

/// <summary>Operator-facing names, icons and units for the mission model, which carries keys only.</summary>
public static class MissionLabels
{
    /// <summary>An item type's name, e.g. "Landing pattern".</summary>
    public static string Of(MissionItemType type) => Text($"ItemType{type}") ?? type.ToString();

    /// <summary>A parameter's label by its key, e.g. "approach-length" → "Approach length".</summary>
    public static string Parameter(string key) => Text($"Param_{key.Replace('-', '_')}") ?? key;

    /// <summary>A choice's label by its key, e.g. "cw" → "Clockwise".</summary>
    public static string Choice(string key) => Text($"Choice_{key}") ?? key;

    /// <summary>What the item does, one sentence.</summary>
    public static string Help(MissionItemType type) => Text($"ItemHelp{type}") ?? string.Empty;

    /// <summary>A phase's picker button: "Takeoff".</summary>
    public static string Button(MissionPhase phase) => Text($"PhaseButton{phase}") ?? phase.ToString();

    /// <summary>The phase's icon.</summary>
    public static string Icon(MissionPhase phase) => phase switch
    {
        MissionPhase.Takeoff => "PlaneTakeoff",
        MissionPhase.Landing => "PlaneLanding",
        _ => "Route"
    };

    /// <summary>A phase's section title.</summary>
    public static string Of(MissionPhase phase) => Text($"Phase{phase}") ?? phase.ToString();

    /// <summary>An end action's name.</summary>
    public static string Of(MissionEndAction action) => Text($"EndAction{action}") ?? action.ToString();

    /// <summary>The unit a quantity is entered in (metric).</summary>
    public static string Unit(Quantity quantity) => quantity switch
    {
        Quantity.Altitude or Quantity.Distance => "m",
        Quantity.Speed => "m/s",
        Quantity.Duration => "s",
        Quantity.Angle => "°",
        _ => string.Empty
    };

    /// <summary>
    /// A parameter's span as a tooltip (the accepted limits, then the vehicle's and why) and as one short line (the
    /// vehicle's span when there is one, else the accepted limits).
    /// </summary>
    public static (string Tip, string Line) Range(ParameterRange range, Quantity quantity)
    {
        var unit = Unit(quantity);
        string N(double value, bool up = false) => (up ? Math.Ceiling(value) : Math.Round(value, Decimals(quantity))).ToString($"F{Decimals(quantity)}", CultureInfo.CurrentCulture);
        var accepted = string.Format(CultureInfo.CurrentCulture, Strings.RangeAccepted, N(range.HardMinimum), N(range.HardMaximum), unit).Trim();
        var vehicle = range.Reason switch
        {
            RangeReason.TurnRadius => string.Format(CultureInfo.CurrentCulture, Strings.RangeTurn, N(range.SoftMinimum ?? 0, up: true), unit,
                range.ReasonValue.ToString("F0", CultureInfo.CurrentCulture)),
            RangeReason.Airspeed => string.Format(CultureInfo.CurrentCulture, Strings.RangeAirspeed, N(range.SoftMinimum ?? 0), N(range.SoftMaximum ?? 0), unit),
            RangeReason.GlideSlope => string.Format(CultureInfo.CurrentCulture, Strings.RangeGlide, N(range.SoftMinimum ?? 0, up: true), unit,
                range.ReasonValue.ToString("F1", CultureInfo.CurrentCulture)),
            RangeReason.ClimbFromPrevious => string.Format(CultureInfo.CurrentCulture, Strings.RangeClimb, N(range.SoftMinimum ?? 0), N(range.SoftMaximum ?? 0), unit,
                range.ReasonValue.ToString("F0", CultureInfo.CurrentCulture)),
            RangeReason.Ground => string.Format(CultureInfo.CurrentCulture, Strings.RangeGround, N(range.ReasonValue), unit),
            _ => null
        };
        return vehicle is null
            ? (accepted, accepted)
            : (accepted + System.Environment.NewLine + vehicle + System.Environment.NewLine + Strings.RangeOutsideNote, vehicle);
    }

    /// <summary>A guide drawing's label on the map, e.g. "min turn 41 m".</summary>
    public static string Guide(MissionGuide guide) => string.Format(CultureInfo.CurrentCulture, guide.Reason switch
    {
        GuideReason.TightestTurn => Strings.GuideTightestTurn,
        GuideReason.SmallestOrbit => Strings.GuideSmallestOrbit,
        GuideReason.ShortestApproach => Strings.GuideShortestApproach,
        GuideReason.ShortestGlide => Strings.GuideShortestGlide,
        GuideReason.AltitudeReached => Strings.GuideAltitudeReached,
        GuideReason.RadiusHandle => Strings.GuideRadiusHandle,
        _ => Strings.GuideApproachHandle
    }, Math.Ceiling(guide.Value).ToString("F0", CultureInfo.CurrentCulture));

    /// <summary>Decimals shown for a quantity.</summary>
    public static int Decimals(Quantity quantity) => quantity switch
    {
        Quantity.Speed => 1,
        Quantity.Number or Quantity.Duration or Quantity.Angle or Quantity.Altitude or Quantity.Distance => 0,
        _ => 0
    };

    /// <summary>The Lucide icon of an item type.</summary>
    public static string Icon(MissionItemType type) => type switch
    {
        MissionItemType.Takeoff => "PlaneTakeoff",
        MissionItemType.Waypoint => "MapPin",
        MissionItemType.LoiterUnlimited => "Infinity",
        MissionItemType.LoiterTime => "Timer",
        MissionItemType.LoiterToAltitude => "Orbit",
        MissionItemType.RegionOfInterest => "Focus",
        MissionItemType.ChangeSpeed => "Gauge",
        MissionItemType.TakePhoto => "Camera",
        MissionItemType.StartVideo => "Video",
        MissionItemType.StopVideo => "VideoOff",
        MissionItemType.CameraTriggerDistance => "Aperture",
        MissionItemType.DelaySeconds => "Hourglass",
        MissionItemType.DoJump => "Redo2",
        MissionItemType.LandingPattern => "PlaneLanding",
        MissionItemType.Land => "ArrowDownToLine",
        _ => "House"
    };

    /// <summary>A short value for an item's row, e.g. "120 m", "120 m AGL" or "→ 3 × 2"; altitudes carry the plan's reference.</summary>
    public static string Summary(MissionItem item, int? jumpTargetNumber, AltitudeReference reference = AltitudeReference.RelativeToHome)
    {
        var culture = CultureInfo.CurrentCulture;
        var unit = MissionAltitudes.UnitOf(reference);
        return item.Type switch
        {
            MissionItemType.ChangeSpeed => string.Format(culture, "{0:F1} m/s", item.Param2),
            MissionItemType.DelaySeconds => string.Format(culture, "{0:F0} s", item.Param1),
            MissionItemType.CameraTriggerDistance => string.Format(culture, "{0:F0} m", item.Param1),
            MissionItemType.DoJump => jumpTargetNumber is { } target ? string.Format(culture, "→ {0} × {1:F0}", target, item.Param2) : "→ ?",
            MissionItemType.LandingPattern when MissionItemValues.LandingGlideSlopeDegrees(item) is { } slope => string.Format(culture, "{0:F0} {1} · {2:F1}°", item.AltitudeMeters, unit, slope),
            _ when item.UsesLocation => string.Format(culture, "{0:F0} {1}", item.AltitudeMeters, unit),
            _ => string.Empty
        };
    }

    private static string? Text(string key) => Strings.ResourceManager.GetString(key, Strings.Culture);
}
