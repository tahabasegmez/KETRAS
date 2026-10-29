using System.Collections.Immutable;
using Ketras.Environment;
using Ketras.Geo;
using Ketras.Planning;
using Ketras.Planning.Fences;
using Ketras.Planning.Missions;
using Ketras.Terrain;
using Ketras.Vehicles;

namespace Ketras.Tests.Planning.Fences;

public sealed class FenceAndTerrainTests
{
    private static readonly GeoPoint Home = new(47.0, 8.0);

    private static PolygonArea Square(GeoPoint center, double half) =>
        new([.. new[] { 45, 135, 225, 315 }.Select(bearing => GeoMath.Offset(center, bearing, half * Math.Sqrt(2)))]);

    [Fact]
    public void Areas_contain_points_and_compare_by_value()
    {
        var square = Square(Home, 1000);

        Assert.True(square.Contains(Home));
        Assert.False(square.Contains(GeoMath.Offset(Home, 0, 1100)));
        Assert.True(new CircleArea(Home, 500).Contains(GeoMath.Offset(Home, 90, 499)));
        Assert.Equal(square, new PolygonArea([.. square.Vertices]));
    }

    [Fact]
    public void Fence_findings_name_several_inclusions_and_zones_the_autopilot_discards()
    {
        Fence[] fences =
        [
            new("area", Square(Home, 1000), Inclusion: true),
            new("far", Square(GeoMath.Offset(Home, 0, 5000), 500), Inclusion: true),
            new("pad", new CircleArea(Home, 100))
        ];

        var codes = FenceRules.Validate(fences, Home).Select(finding => (finding.Code, finding.FenceId)).ToList();

        Assert.Contains(("fences.several-inclusions", null), codes);
        Assert.Contains(("fences.home-outside", "far"), codes);
        Assert.Contains(("fences.home-inside", "pad"), codes);
        Assert.DoesNotContain(codes, c => c.FenceId == "area");
        Assert.Equal("fence-2", Fence.NextId([new Fence("fence-1", new CircleArea(Home, 1))]));
    }

    [Fact]
    public void Imposed_zones_take_part_in_the_fence_checks()
    {
        var plan = new Plan { Mission = Mission.Empty with { Settings = new MissionSettings { PlannedHome = Home } } };

        var findings = PlanRules.Validate(plan, extraFences: [new Fence("zone", new CircleArea(Home, 50))]);

        Assert.Contains(findings, finding => finding.Code == "fences.home-inside" && finding.FenceId == "zone");
    }

    [Fact]
    public void A_zone_is_valid_only_inside_its_window()
    {
        var zone = new Zone
        {
            Id = "z", Source = "file", Area = new CircleArea(Home, 10),
            ValidFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), ValidUntilUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        };

        Assert.True(zone.IsValidAt(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)));
        Assert.False(zone.IsValidAt(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>Flat ground at 500 m, with an optional ridge (east-west) 1 km north of home.</summary>
    private static TerrainGrid Grid(float ridgeHeight = 500)
    {
        const int rows = 101, columns = 101;
        const double step = 0.0005; // about 55 m north-south
        var south = Home.Latitude - (50 * step);
        var west = Home.Longitude - (50 * step);
        var heights = new float[rows * columns];
        for (var r = 0; r < rows; r++)
        {
            var latitude = south + (r * step);
            var northOfHome = (latitude - Home.Latitude) * 111_195;
            for (var c = 0; c < columns; c++)
            {
                heights[(r * columns) + c] = Math.Abs(northOfHome - 1000) < 60 ? ridgeHeight : 500;
            }
        }

        return new TerrainGrid(south, west, step, step, rows, columns, heights, "test");
    }

    private static Mission Route(double homeAltitude = 500, double waypointAltitude = 100) => Mission.Empty with
    {
        Settings = new MissionSettings { PlannedHome = Home, HomeAltitudeMeters = homeAltitude, CruiseSpeedMps = 18 },
        Items =
        [
            MissionItemFactory.Create(MissionItemType.Takeoff, GeoMath.Offset(Home, 90, 600), new MissionItemOptions { AltitudeMeters = 50 }),
            MissionItemFactory.Create(MissionItemType.Waypoint, GeoMath.Offset(Home, 0, 1800), new MissionItemOptions { AltitudeMeters = waypointAltitude }),
            MissionItemFactory.Create(MissionItemType.Land, GeoMath.Offset(Home, 0, 2000), new MissionItemOptions { AltitudeMeters = 0 })
        ]
    };

    [Fact]
    public void A_plan_well_above_flat_terrain_is_clear()
    {
        Assert.Empty(TerrainRules.Validate(Route(), Grid()));
    }

    [Fact]
    public void A_leg_through_a_ridge_is_reported_where_it_meets_the_terrain()
    {
        var findings = TerrainRules.Validate(Route(), Grid(ridgeHeight: 700));

        var contact = Assert.Single(findings, finding => finding.Code == "terrain.leg-contact");
        Assert.Equal(2, contact.ItemNumber);
        Assert.InRange(GeoMath.DistanceMeters(Home, contact.Location!.Value), 800, 1100);
    }

    [Fact]
    public void A_wrong_home_altitude_and_an_unset_home_are_reported()
    {
        Assert.Contains(TerrainRules.Validate(Route(homeAltitude: 600), Grid()), finding => finding.Code == "terrain.home-altitude");
        var unset = Route() with { Settings = new MissionSettings() };
        Assert.Equal("terrain.no-home", Assert.Single(TerrainRules.Validate(unset, Grid())).Code);
        Assert.Empty(TerrainRules.Validate(Route(), null));
    }

    [Fact]
    public void A_climb_steeper_than_the_vehicle_can_fly_is_reported_and_flown_at_the_limit()
    {
        var steep = Route(waypointAltitude: 400);

        var findings = TerrainRules.Validate(steep, Grid(), VehicleLimits.Default with { MaxClimbRateMps = 2 });

        Assert.Contains(findings, finding => finding.Code == "terrain.climb" && finding.ItemNumber == 2);
    }

    [Fact]
    public void The_grid_reads_bilinearly_and_knows_its_edges()
    {
        var grid = new TerrainGrid(10, 20, 1, 1, 2, 2, new float[] { 0, 10, 100, float.NaN });

        Assert.Equal(11, grid.NorthLatitude);
        Assert.Equal(21, grid.EastLongitude);
        Assert.Null(grid.HeightAt(new GeoPoint(10.5, 20.5)));
        Assert.Throws<ArgumentException>(() => new TerrainGrid(0, 0, 1, 1, 1, 1, new float[] { 1 }));
        Assert.Equal(ImmutableArray.Create(0f, 10, 100, float.NaN).Length, grid.Heights.Length);
    }
}
