using System.Globalization;
using Ketras.Model.Planning;
using Ketras.Model.Vehicles;

namespace Ketras.Model.Missions;

/// <summary>
/// Rules: whether the mission fits the battery and the wind. Findings: the energy margin below
/// <see cref="EnergyCalculator.WarningMarginPercent"/> or negative ("mission.energy"), and a stretch whose headwind reaches
/// <see cref="HeadwindRatio"/> of the airspeed ("mission.headwind").
/// </summary>
public static class MissionBudgetRules
{
    /// <summary>A headwind of this share of the airspeed is a finding.</summary>
    public const double HeadwindRatio = 0.8;

    /// <summary>The findings for an estimate and its energy budget (null when the budget is unknown).</summary>
    public static IReadOnlyList<PlanFinding> Validate(MissionEstimate estimate, EnergyBudget? budget)
    {
        var findings = new List<PlanFinding>();
        if (budget is { } b && b.MarginPercent < EnergyCalculator.WarningMarginPercent)
        {
            findings.Add(new PlanFinding(FindingSource.Flight, "mission.energy", b.MarginMah < 0
                ? Format("The battery does not last: the mission needs {0:F0} mAh and {1:F0} mAh is left above the low-battery threshold.", b.NeededMah, b.UsableMah)
                : Format("Only {0:F0} % of the battery above the low-battery threshold is left at the end ({1:F0} mAh).", b.MarginPercent, b.MarginMah)));
        }

        if (estimate.StrongestHeadwind is { HeadwindRatio: >= HeadwindRatio } leg)
        {
            findings.Add(new PlanFinding(FindingSource.Flight, "mission.headwind", leg.HeadwindRatio >= 1
                ? Format("The headwind on the way to {0} is stronger than the airspeed; the aircraft cannot make progress.", Target(leg))
                : Format("The headwind on the way to {0} is {1:F0} % of the airspeed; progress there is slow.", Target(leg), leg.HeadwindRatio * 100),
                leg.ItemIndex >= 0 ? leg.ItemIndex + 1 : null));
        }

        return findings;
    }

    private static string Target(MissionLeg leg) => leg.ItemIndex >= 0 ? Format("item {0}", leg.ItemIndex + 1) : "home";

    private static string Format(string format, params object[] values) => string.Format(CultureInfo.InvariantCulture, format, values);
}
