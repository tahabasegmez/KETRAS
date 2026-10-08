using System.Net;
using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Station.Missions;
using Ketras.Station.Notifications;
using Ketras.Station.Settings;
using Ketras.Station.Terrain;
using Ketras.Terrain;
using Ketras.Terrain.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ketras.Station.Tests;

public sealed class TerrainTests : IDisposable
{
    // A synthetic N47E008 cell, 0.01° per sample, rising 1 m per sample southwards from 400 m at 48° N.
    private static readonly GeoPoint Home = new(47.5, 8.5);
    private static readonly GeoPoint Touchdown = new(47.4, 8.5);
    private readonly string _directory = System.IO.Directory.CreateTempSubdirectory("ketras-terrain-").FullName;

    public void Dispose() => System.IO.Directory.Delete(_directory, recursive: true);

    private (PlanStore Plans, TerrainService Terrain) Create()
    {
        var tiles = new TerrainTileStore(_directory, new HttpClient(new NotFound()));
        SyntheticGeoTiff.Write(tiles.TilePath(47, 8), 101, 101, 32, 48, 8, 0.01, (row, _) => 400 + row);
        var plans = new PlanStore(new NotificationStore(TimeProvider.System));
        var options = Options.Create(new StationOptions { Terrain = { Enabled = true } });
        return (plans, new TerrainService(plans, options, tiles, TimeProvider.System, NullLogger<TerrainService>.Instance));
    }

    [Fact]
    public void Alignment_puts_home_on_the_ground_and_touchdowns_relative_to_it()
    {
        var mission = MissionEditor.UpdateSettings(new Mission(), s => s with { PlannedHome = Home, HomeAltitudeMeters = 300 });
        mission = MissionEditor.Add(mission, MissionItemType.Land, Touchdown, null, null).Mission;

        var (aligned, changes) = TerrainAlignment.Align(mission, p => p == Home ? 450 : p == Touchdown ? 462.34 : null);
        var (again, none) = TerrainAlignment.Align(aligned, p => p == Home ? 450 : p == Touchdown ? 462.34 : null);
        var (unknown, _) = TerrainAlignment.Align(mission, _ => null);

        Assert.Equal(450, aligned.Settings.HomeAltitudeMeters);
        Assert.Equal(12.3, aligned.Items[0].AltitudeMeters);
        Assert.Equal(["home altitude 300.0 → 450.0 m", "item 1 touchdown 120.0 → 12.3 m"], changes);
        Assert.Same(aligned, again);
        Assert.Empty(none);
        Assert.Same(mission, unknown);
    }

    [Fact]
    public async Task While_editing_the_plan_follows_the_terrain_on_disk()
    {
        var (plans, terrain) = Create();
        using var _ = terrain;
        TerrainGridChanged(terrain, out var gridChanged);

        plans.UpdateSettings(s => s with { PlannedHome = Home });
        Assert.NotNull(plans.AddItem(MissionItemType.Land, Touchdown));

        await StationFixture.WaitForAsync(() => plans.Current.Mission.Settings.HomeAltitudeMeters == 450);
        await StationFixture.WaitForAsync(() => plans.Current.Mission.Items[0].AltitudeMeters == 10);
        Assert.True(gridChanged.Task.IsCompleted);
        Assert.Equal(450, terrain.GroundAt(Home)!.Value, 1);
    }

    [Fact]
    public async Task In_a_flat_simulator_world_the_ground_is_level_with_the_home_and_the_touchdown_follows()
    {
        var (plans, terrain) = Create();
        using var _ = terrain;
        plans.UpdateSettings(s => s with { PlannedHome = Home });
        Assert.NotNull(plans.AddItem(MissionItemType.Land, Touchdown));
        await StationFixture.WaitForAsync(() => plans.Current.Mission.Items[0].AltitudeMeters == 10);

        terrain.UseFlatGround(true);
        await StationFixture.WaitForAsync(() => plans.Current.Mission.Items[0].AltitudeMeters == 0);
        Assert.Equal(450, terrain.GroundAt(Touchdown)!.Value, 1);
        Assert.Equal(450, terrain.LoadGrid(new GeoBounds(47.3, 8.4, 47.6, 8.6))!.HeightAt(Touchdown.Latitude, Touchdown.Longitude)!.Value, 1);
        Assert.All(terrain.Grid!.Heights.ToArray(), h => Assert.Equal(450, h, 1));
        Assert.Equal("flat ground at the home (simulator world without terrain)", (await terrain.PrepareForUploadAsync(plans.Current, [])).Summary);

        terrain.UseFlatGround(false);
        await StationFixture.WaitForAsync(() => plans.Current.Mission.Items[0].AltitudeMeters == 10);
    }

    [Fact]
    public void Flat_ground_holds_while_a_simulator_runs_a_world_without_terrain_and_no_other_link_is_up()
    {
        var simulator = new LinkAndVehicleTests.FakeSimulator();
        var link = new Links.LinkStatus(Links.LinkState.Connected, new Links.LinkProfile { Name = "sim", Kind = Links.LinkKind.Simulator, SimulatorId = "fake" });
        var radio = new Links.LinkStatus(Links.LinkState.Connected, new Links.LinkProfile { Name = "radio", Kind = Links.LinkKind.Serial });

        Assert.False(SimulatorGroundWorker.IsFlat(Links.LinkStatus.Disconnected, [simulator]));
        simulator.Run("flat");
        Assert.True(SimulatorGroundWorker.IsFlat(Links.LinkStatus.Disconnected, [simulator]));
        Assert.True(SimulatorGroundWorker.IsFlat(link, [simulator]));
        Assert.False(SimulatorGroundWorker.IsFlat(radio, [simulator]));
        simulator.Run("terrain");
        Assert.False(SimulatorGroundWorker.IsFlat(link, [simulator]));
    }

    [Fact]
    public async Task Before_an_upload_the_plan_is_aligned_and_the_summary_says_what_was_checked()
    {
        var (plans, terrain) = Create();
        using var _ = terrain;
        plans.UpdateSettings(s => s with { PlannedHome = Home });
        plans.AddItem(MissionItemType.Land, Touchdown);

        var prepared = await terrain.PrepareForUploadAsync(plans.Current, []);
        var far = await terrain.PrepareForUploadAsync(new Model.Planning.Plan { Mission = MissionEditor.UpdateSettings(new Mission(), s => s with { PlannedHome = new GeoPoint(-30.5, 20.5) }) }, []);
        var empty = await terrain.PrepareForUploadAsync(new Model.Planning.Plan(), []);

        Assert.Equal("terrain data complete", prepared.Summary);
        Assert.Equal(450, prepared.Plan.Mission.Settings.HomeAltitudeMeters);
        Assert.Equal(10, prepared.Plan.Mission.Items[0].AltitudeMeters);
        Assert.Equal("terrain data missing (S31E020 not published)", far.Summary);
        Assert.Equal("terrain not checked: the plan has no positions", empty.Summary);
    }

    private static void TerrainGridChanged(TerrainService terrain, out TaskCompletionSource changed)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        terrain.GridChanged += grid =>
        {
            if (grid is not null)
            {
                source.TrySetResult();
            }
        };
        changed = source;
    }

    /// <summary>The bucket has no cell beyond the ones on disk.</summary>
    private sealed class NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
