using Ketras.Model.Geo;
using Ketras.Model.Missions;

namespace Ketras.Tests.Planning.Missions;

public sealed class MissionAltitudesTests
{
    private static readonly GeoPoint Home = new(47.4, 8.5);
    private static readonly GeoPoint North = GeoMath.Offset(Home, 0, 1000);

    // The ground rises 30 m from 890 m north of the home, which sits at 400 m.
    private static double? Ground(GeoPoint point) => point.Latitude > Home.Latitude + 0.008 ? 430 : 400;

    private static Mission Plan(AltitudeReference reference, double waypointAltitude)
    {
        var mission = MissionEditor.UpdateSettings(new Mission(), s => s with { PlannedHome = Home, HomeAltitudeMeters = 400, AltitudeReference = reference });
        return MissionEditor.Add(mission, MissionItemType.Waypoint, North, new MissionItemOptions { AltitudeMeters = waypointAltitude }, null).Mission;
    }

    [Fact]
    public void Each_reference_gives_the_height_above_sea_level()
    {
        Assert.Equal(500, MissionAltitudes.AmslOf(Plan(AltitudeReference.RelativeToHome, 100).Settings, 100, North, Ground));
        Assert.Equal(100, MissionAltitudes.AmslOf(Plan(AltitudeReference.AboveSeaLevel, 100).Settings, 100, North, Ground));
        Assert.Equal(530, MissionAltitudes.AmslOf(Plan(AltitudeReference.AboveTerrain, 100).Settings, 100, North, Ground));
        Assert.Null(MissionAltitudes.AmslOf(Plan(AltitudeReference.AboveTerrain, 100).Settings, 100, North, null));
        Assert.Equal("m AGL", MissionAltitudes.UnitOf(AltitudeReference.AboveTerrain));
    }

    [Fact]
    public void Switching_the_reference_keeps_every_height_above_sea_level()
    {
        var relative = Plan(AltitudeReference.RelativeToHome, 100);

        var terrain = MissionAltitudes.Convert(relative, AltitudeReference.AboveTerrain, Ground, out var failed)!;
        var amsl = MissionAltitudes.Convert(terrain, AltitudeReference.AboveSeaLevel, Ground, out _)!;
        var back = MissionAltitudes.Convert(amsl, AltitudeReference.RelativeToHome, Ground, out _)!;

        Assert.Null(failed);
        Assert.Equal((AltitudeReference.AboveTerrain, 70), (terrain.Settings.AltitudeReference, terrain.Items[0].AltitudeMeters));
        Assert.Equal(500, amsl.Items[0].AltitudeMeters);
        Assert.Equal(100, back.Items[0].AltitudeMeters);
        Assert.Null(MissionAltitudes.Convert(relative, AltitudeReference.AboveTerrain, _ => null, out failed));
        Assert.Equal(1, failed);
    }

    [Fact]
    public void A_landing_pattern_converts_its_approach_on_the_orbit_and_its_touchdown_on_the_ground()
    {
        var mission = MissionEditor.UpdateSettings(new Mission(), s => s with { PlannedHome = Home, HomeAltitudeMeters = 400 });
        mission = MissionEditor.Add(mission, MissionItemType.LandingPattern, North, new MissionItemOptions { AltitudeMeters = 70, HeadingDegrees = 0 }, null).Mission;
        mission = MissionEditor.Update(mission, mission.Items[0].Id, item => item with { Param1 = 30 }).Mission;

        var terrain = MissionAltitudes.Convert(mission, AltitudeReference.AboveTerrain, Ground, out _)!;

        // The orbit centre lies south of the touchdown, over the home's 400 m ground; the touchdown over 430 m.
        Assert.Equal((70, 0), (terrain.Items[0].AltitudeMeters, terrain.Items[0].Param1));
    }

    [Fact]
    public void A_landing_set_to_abort_on_a_rangefinder_dropout_is_a_finding()
    {
        var mission = MissionEditor.UpdateSettings(new Mission(), s => s with { PlannedHome = Home });
        mission = MissionEditor.Add(mission, MissionItemType.LandingPattern, North, new MissionItemOptions { HeadingDegrees = 0 }, null).Mission;

        Assert.DoesNotContain(MissionRules.Validate(mission), f => f.Code == "mission.landing-abort");

        var aborting = MissionEditor.UpdateSettings(mission, s => s with { Landing = s.Landing with { AbortIfTerrainLost = true } });
        var finding = Assert.Single(MissionRules.Validate(aborting), f => f.Code == "mission.landing-abort");
        Assert.Contains("loses the ground for a second", finding.Message, StringComparison.Ordinal);

        var unused = MissionEditor.UpdateSettings(aborting, s => s with { Landing = s.Landing with { TerrainUse = LandingTerrainUse.Off } });
        Assert.DoesNotContain(MissionRules.Validate(unused), f => f.Code == "mission.landing-abort");
    }

    [Fact]
    public void Glide_slopes_are_checked_above_sea_level_in_an_above_terrain_plan()
    {
        var mission = MissionEditor.UpdateSettings(new Mission(), s => s with { PlannedHome = Home, HomeAltitudeMeters = 400, AltitudeReference = AltitudeReference.AboveTerrain });
        mission = MissionEditor.Add(mission, MissionItemType.LandingPattern, North, new MissionItemOptions { AltitudeMeters = 40, HeadingDegrees = 0 }, null).Mission;

        // 40 m AGL over the 400 m orbit and 0 m AGL on the 430 m touchdown: only 10 m to lose, a shallow slope.
        Assert.DoesNotContain(MissionRules.Validate(mission, groundAt: Ground), f => f.Code is "mission.landing-slope" or "mission.landing-height");

        // Without terrain the height is unknown and nothing is claimed.
        Assert.DoesNotContain(MissionRules.Validate(mission), f => f.Code is "mission.landing-slope" or "mission.landing-height");
    }
}
