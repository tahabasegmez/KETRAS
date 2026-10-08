using Ketras.Model.Geo;

namespace Ketras.Model.Missions;

/// <summary>One stretch of the estimate: what it takes to complete an item from the previous one.</summary>
/// <param name="ItemIndex">The item (0-based); -1 for the return to the home after the last item.</param>
/// <param name="DistanceMeters">Distance flown.</param>
/// <param name="Seconds">Time, flying and waiting.</param>
/// <param name="HeadwindRatio">Headwind over airspeed on this stretch (0 without wind); 1 and above cannot be flown.</param>
public sealed record MissionLeg(int ItemIndex, double DistanceMeters, double Seconds, double HeadwindRatio);

/// <summary>How long and how far a mission is, item by item.</summary>
/// <param name="Legs">The stretches in flying order.</param>
/// <param name="IsOpenEnded">The mission ends in an unlimited loiter, so its end time is the operator's.</param>
public sealed record MissionEstimate(IReadOnlyList<MissionLeg> Legs, bool IsOpenEnded)
{
    /// <summary>The whole distance.</summary>
    public double DistanceMeters => Legs.Sum(l => l.DistanceMeters);

    /// <summary>The whole time.</summary>
    public double Seconds => Legs.Sum(l => l.Seconds);

    /// <summary>The time from the start of item <paramref name="itemIndex"/> to the end (the return to home included).</summary>
    public double SecondsFrom(int itemIndex) => Legs.Where(l => l.ItemIndex >= itemIndex || l.ItemIndex < 0).Sum(l => l.Seconds);

    /// <summary>The stretch with the strongest headwind, if any has one.</summary>
    public MissionLeg? StrongestHeadwind => Legs.Where(l => l.HeadwindRatio > 0).MaxBy(l => l.HeadwindRatio);
}

/// <summary>
/// Calculator: the mission's flight time and distance. Each item is reached in a straight line from the previous
/// position at the airspeed in force (the cruise speed until a speed change), corrected for a steady wind by the wind
/// triangle; loiter times and delays add their seconds; a landing pattern flies to its approach start and down its
/// approach; the return to home is added when the mission ends that way. Jumps are not repeated and orbits up to an
/// altitude are not counted, so the estimate is a lower bound there.
/// </summary>
public static class MissionTimeCalculator
{
    /// <summary>Estimates <paramref name="mission"/>; the wind is where the air goes (north, east, m/s).</summary>
    public static MissionEstimate Estimate(Mission mission, double windNorthMps = 0, double windEastMps = 0)
    {
        var settings = mission.Settings;
        var home = settings.PlannedHome.IsUnset ? (GeoPoint?)null : settings.PlannedHome;
        var position = home;
        var speed = settings.CruiseSpeedMps;
        var legs = new List<MissionLeg>();
        var landed = false;
        var openEnded = false;

        MissionLeg Fly(int index, GeoPoint to, double extraSeconds = 0)
        {
            if (position is not { } from)
            {
                position = to;
                return new MissionLeg(index, 0, extraSeconds, 0);
            }

            var distance = GeoMath.DistanceMeters(from, to);
            var (groundSpeed, headwind) = GroundSpeed(speed, GeoMath.BearingDegrees(from, to), windNorthMps, windEastMps);
            position = to;
            return new MissionLeg(index, distance, (distance < 0.5 ? 0 : distance / groundSpeed) + extraSeconds, distance < 0.5 ? 0 : headwind);
        }

        for (var i = 0; i < mission.Items.Count && !landed && !openEnded; i++)
        {
            var item = mission.Items[i];
            switch (item.Type)
            {
                case MissionItemType.ChangeSpeed when item.Param2 > 0:
                    speed = item.Param2;
                    break;
                case MissionItemType.DelaySeconds:
                    legs.Add(new MissionLeg(i, 0, Math.Max(0, item.Param1), 0));
                    break;
                case MissionItemType.LoiterTime when item.Location is { } at:
                    legs.Add(Fly(i, at, Math.Max(0, item.Param1)));
                    break;
                case MissionItemType.LoiterUnlimited when item.Location is { } at:
                    legs.Add(Fly(i, at));
                    openEnded = true;
                    break;
                case MissionItemType.LandingPattern when item.Location is { } touchdown:
                    var start = MissionItemValues.LandingPatternApproachStart(item) ?? touchdown;
                    var toStart = Fly(i, start);
                    var final = Fly(i, touchdown);
                    legs.Add(toStart with
                    {
                        DistanceMeters = toStart.DistanceMeters + final.DistanceMeters, Seconds = toStart.Seconds + final.Seconds,
                        HeadwindRatio = Math.Max(toStart.HeadwindRatio, final.HeadwindRatio)
                    });
                    landed = true;
                    break;
                case MissionItemType.Land when item.Location is { } at:
                    legs.Add(Fly(i, at));
                    landed = true;
                    break;
                case MissionItemType.ReturnToLaunch when home is { } h:
                    legs.Add(Fly(i, h));
                    landed = true;
                    break;
                default:
                    if (item.UsesLocation && item.Location is { } location)
                    {
                        legs.Add(Fly(i, location));
                    }

                    break;
            }
        }

        if (!landed && !openEnded && settings.EndAction == MissionEndAction.ReturnToLaunch && home is { } back && legs.Count > 0)
        {
            legs.Add(Fly(-1, back));
        }

        return new MissionEstimate(legs, openEnded);
    }

    /// <summary>
    /// Ground speed along a track at an airspeed in a steady wind (where the air goes), and the headwind as a share of
    /// the airspeed. A crosswind as strong as the airspeed leaves a crawl of 0.1 m/s rather than no answer.
    /// </summary>
    public static (double GroundSpeedMps, double HeadwindRatio) GroundSpeed(double airspeedMps, double trackDegrees, double windNorthMps, double windEastMps)
    {
        var airspeed = Math.Max(airspeedMps, 0.1);
        var track = trackDegrees * Math.PI / 180;
        var along = (windNorthMps * Math.Cos(track)) + (windEastMps * Math.Sin(track));
        var across = (-windNorthMps * Math.Sin(track)) + (windEastMps * Math.Cos(track));
        var crab = (airspeed * airspeed) - (across * across);
        var groundSpeed = (crab > 0 ? Math.Sqrt(crab) : 0) + along;
        return (Math.Max(groundSpeed, 0.1), Math.Max(0, -along) / airspeed);
    }
}
