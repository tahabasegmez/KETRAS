using System.Globalization;
using Ketras.Geo;
using Ketras.Planning;
using Ketras.Planning.Missions;
using Ketras.Vehicles;

namespace Ketras.Terrain;

/// <summary>
/// Checks a plan against terrain. Altitudes are the plan's: home altitude + relative item altitude. The vehicle is
/// assumed to fly straight legs with a linear altitude ramp; a climb steeper than its climb rate allows is followed at
/// that limit instead. Contact is flagged with no margin. See docs/MISSION.md §10.
/// </summary>
public static class TerrainRules
{
    /// <summary>Distance between terrain samples along a line, about the spacing of 1 arc-second data.</summary>
    private const double SampleSpacingMeters = 30;

    /// <summary>Ground contact near the runway is expected; this much of a takeoff or landing leg is not checked.</summary>
    private const double RunwayIgnoreMeters = 10;

    private const double HomeMismatchMeters = 30;
    private const double TouchdownMismatchMeters = 10;
    private const int OrbitSamples = 36;
    private const double ReturnSampleSpacingMeters = 500;

    /// <summary>Terrain findings for the mission; empty without a grid.</summary>
    public static IReadOnlyList<PlanFinding> Validate(Mission mission, TerrainGrid? grid, VehicleLimits? limits = null) =>
        grid is null ? [] : new Checker(mission, grid, limits ?? VehicleLimits.Default).Run();

    /// <summary>Walks the plan once, keeping the current position, altitude and speed.</summary>
    private sealed class Checker(Mission mission, TerrainGrid grid, VehicleLimits limits)
    {
        private readonly List<PlanFinding> _findings = [];
        private readonly List<(GeoPoint From, GeoPoint To, double FromAlt, double ToAlt, int Item)> _legs = [];
        private readonly double _homeAlt = mission.Settings.HomeAltitudeMeters;
        private double _speed = mission.Settings.CruiseSpeedMps;

        public IReadOnlyList<PlanFinding> Run()
        {
            var home = mission.Settings.PlannedHome;
            if (home.IsUnset)
            {
                Add("terrain.no-home", "Terrain not checked: the planned home is not set.");
                return _findings;
            }

            if (grid.HeightAt(home) is not { } homeGround)
            {
                Add("terrain.no-data-home", "No terrain data at the planned home.", null, home);
            }
            else if (Math.Abs(homeGround - _homeAlt) > HomeMismatchMeters)
            {
                Add("terrain.home-altitude", Format(
                    "Home altitude {0:F0} m differs from the terrain there ({1:F0} m) by {2:F0} m; every altitude check is off by as much.",
                    _homeAlt, homeGround, _homeAlt - homeGround), null, home);
            }

            var position = home;
            var altitude = _homeAlt;
            var items = mission.Items;

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var number = i + 1;

                switch (item.Type)
                {
                    case MissionItemType.ChangeSpeed:
                        _speed = item.Param2;
                        break;

                    case MissionItemType.DoJump when (int)item.Param1 is var target && target >= 0 && target < items.Count
                                                     && items[target].Location is { } jumpTo:
                        CheckLeg(position, jumpTo, altitude, AltitudeOf(items[target]), number, climbLimited: true, 0, 0);
                        break;

                    case MissionItemType.ReturnToLaunch:
                        var returnAlt = Math.Max(altitude, _homeAlt + (limits.ReturnAltitudeMeters ?? 0));
                        CheckLeg(position, home, returnAlt, returnAlt, number, climbLimited: false, 0, 0, "the return to launch");
                        position = home;
                        altitude = _homeAlt;
                        break;

                    case MissionItemType.LandingPattern when item.Location is { } touchdown
                                                             && MissionItemValues.LandingPatternOrbitCenter(item) is { } center
                                                             && MissionItemValues.LandingPatternApproachStart(item) is { } start:
                        var approachAlt = AltitudeOf(item);
                        var touchdownAlt = MissionAltitudes.TouchdownAmslOf(mission.Settings, item, grid.HeightAt) ?? _homeAlt + item.Param1;
                        CheckLeg(position, center, altitude, approachAlt, number, climbLimited: true, 0, 0);
                        CheckOrbit(center, Math.Abs(item.Param2), approachAlt, number, "landing orbit");
                        CheckLeg(start, touchdown, approachAlt, touchdownAlt, number, climbLimited: false, 0, RunwayIgnoreMeters, "the glide path");
                        CheckTouchdown(touchdown, touchdownAlt, number);
                        position = touchdown;
                        altitude = touchdownAlt;
                        break;

                    case MissionItemType.RegionOfInterest:
                        break;

                    default:
                        if (item.Location is not { } location)
                        {
                            break;
                        }

                        var targetAlt = AltitudeOf(item);
                        var isTakeoff = item.Type == MissionItemType.Takeoff;
                        var isLand = item.Type == MissionItemType.Land;
                        CheckLeg(position, location, altitude, targetAlt, number, climbLimited: !isLand,
                            isTakeoff ? RunwayIgnoreMeters : 0, isLand ? RunwayIgnoreMeters : 0);

                        if (MissionItemValues.LoiterRadiusMeters(item) is { } radius and > 0)
                        {
                            CheckOrbit(location, radius, targetAlt, number, "orbit");
                        }

                        if (isLand)
                        {
                            CheckTouchdown(location, targetAlt, number);
                        }

                        position = location;
                        altitude = targetAlt;
                        break;
                }
            }

            CheckReturnPaths(home);
            return _findings;
        }

        /// <summary>The height above mean sea level an item flies at, in the plan's altitude reference.</summary>
        private double AltitudeOf(MissionItem item) => MissionAltitudes.AmslOf(mission.Settings, item, grid.HeightAt) ?? _homeAlt + item.AltitudeMeters;

        private void Add(string code, string message, int? item = null, GeoPoint? location = null) =>
            _findings.Add(new PlanFinding(FindingSource.Terrain, code, message, item, location));

        private void CheckLeg(GeoPoint from, GeoPoint to, double fromAlt, double toAlt, int number, bool climbLimited,
            double ignoreStart, double ignoreEnd, string what = "the leg")
        {
            var distance = GeoMath.DistanceMeters(from, to);
            if (distance < 1)
            {
                return;
            }

            _legs.Add((from, to, fromAlt, toAlt, number));
            var gradient = (toAlt - fromAlt) / distance;
            var speed = Math.Max(_speed, 1);
            var climbGradient = limits.MaxClimbRateMps / speed;
            var sinkGradient = limits.MaxSinkRateMps / speed;
            var lagging = climbLimited && gradient > climbGradient;

            if (lagging)
            {
                Add("terrain.climb", Format(
                    "Item {0}: {1} climbs {2:F0}% but the aircraft manages about {3:F0}% at {4:F0} m/s; it arrives {5:F0} m low.",
                    number, what, gradient * 100, climbGradient * 100, speed, toAlt - fromAlt - (climbGradient * distance)), number, to);
            }
            else if (climbLimited && -gradient > sinkGradient)
            {
                Add("terrain.descent", Format(
                    "Item {0}: {1} descends {2:F0}% but the aircraft sinks about {3:F0}% at {4:F0} m/s; it arrives high.",
                    number, what, -gradient * 100, sinkGradient * 100, speed), number, to);
            }

            var bearing = GeoMath.BearingDegrees(from, to);
            var samples = Math.Max(1, (int)Math.Ceiling(distance / SampleSpacingMeters));
            var noData = false;
            for (var k = 0; k <= samples; k++)
            {
                var x = distance * k / samples;
                if (x < ignoreStart || x > distance - ignoreEnd)
                {
                    continue;
                }

                var point = GeoMath.Offset(from, bearing, x);
                var pathAlt = lagging ? Math.Min(fromAlt + (climbGradient * x), toAlt) : fromAlt + (gradient * x);
                if (grid.HeightAt(point) is not { } ground)
                {
                    noData = true;
                    continue;
                }

                if (ground > pathAlt)
                {
                    Add("terrain.leg-contact", Format(
                        "Item {0}: {1} meets terrain {2:F0} m from its start (ground {3:F0} m, path {4:F0} m AMSL).",
                        number, what, x, ground, pathAlt), number, point);
                    return;
                }
            }

            if (noData)
            {
                Add("terrain.no-data-leg", Format("Item {0}: no terrain data along {1}.", number, what), number, to);
            }
        }

        private void CheckOrbit(GeoPoint center, double radius, double altitude, int number, string what)
        {
            for (var k = 0; k < OrbitSamples; k++)
            {
                var point = GeoMath.Offset(center, k * 360.0 / OrbitSamples, radius);
                if (grid.HeightAt(point) is { } ground && ground > altitude)
                {
                    Add("terrain.orbit-contact", Format(
                        "Item {0}: the {1} ({2:F0} m radius) meets terrain (ground {3:F0} m, orbit {4:F0} m AMSL).",
                        number, what, radius, ground, altitude), number, point);
                    return;
                }
            }
        }

        private void CheckTouchdown(GeoPoint touchdown, double altitude, int number)
        {
            if (grid.HeightAt(touchdown) is { } ground && Math.Abs(ground - altitude) > TouchdownMismatchMeters)
            {
                Add("terrain.touchdown-altitude", Format(
                    "Item {0}: touchdown is planned at {1:F0} m AMSL but the ground there is {2:F0} m.",
                    number, altitude, ground), number, touchdown);
            }
        }

        /// <summary>
        /// Return to launch from points along every leg: climb in place to max(current, home + return altitude), then a
        /// straight line home at that altitude. One finding per leg at most.
        /// </summary>
        private void CheckReturnPaths(GeoPoint home)
        {
            if (limits.ReturnAltitudeMeters is not { } returnHeight)
            {
                return;
            }

            foreach (var (from, to, fromAlt, toAlt, number) in _legs)
            {
                var distance = GeoMath.DistanceMeters(from, to);
                var bearing = GeoMath.BearingDegrees(from, to);
                var steps = Math.Max(1, (int)Math.Ceiling(distance / ReturnSampleSpacingMeters));
                for (var k = 0; k <= steps; k++)
                {
                    var x = distance * k / steps;
                    var start = GeoMath.Offset(from, bearing, x);
                    var altitude = Math.Max(fromAlt + ((toAlt - fromAlt) * x / distance), _homeAlt + returnHeight);
                    if (FirstContact(start, home, altitude) is { } contact)
                    {
                        Add("terrain.return-contact", Format(
                            "Item {0}: a return to launch from this leg at {1:F0} m AMSL meets terrain on the way home.",
                            number, altitude), number, contact);
                        break;
                    }
                }
            }
        }

        private GeoPoint? FirstContact(GeoPoint from, GeoPoint to, double altitude)
        {
            var distance = GeoMath.DistanceMeters(from, to);
            var bearing = GeoMath.BearingDegrees(from, to);
            var samples = (int)Math.Ceiling(distance / SampleSpacingMeters);
            for (var k = 1; k <= samples; k++)
            {
                var point = GeoMath.Offset(from, bearing, distance * k / samples);
                if (grid.HeightAt(point) is { } ground && ground > altitude)
                {
                    return point;
                }
            }

            return null;
        }

        private static string Format(string format, params object[] args) => string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
