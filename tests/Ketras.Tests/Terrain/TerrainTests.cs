using System.Net;
using Ketras.Geo;
using Ketras.Notifications;
using Ketras.Planning;
using Ketras.Planning.Missions;
using Ketras.Settings;
using Ketras.Terrain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ketras.Tests.Terrain;

public sealed class TerrainTests : IDisposable
{
    // A synthetic N47E008 cell, 0.01° per sample, rising 1 m per sample southwards from 400 m at 48° N.
    private static readonly GeoPoint Home = new(47.5, 8.5);
    private static readonly GeoPoint Touchdown = new(47.4, 8.5);
    private readonly string _directory = System.IO.Directory.CreateTempSubdirectory("ketras-terrain-").FullName;

    public void Dispose() => System.IO.Directory.Delete(_directory, recursive: true);

    // Just south of the synthetic cell: the 2 km margin reaches N46E008, which is not on disk.
    private static readonly GeoPoint Edge = new(47.005, 8.5);
    private readonly NotFound _bucket = new();

    private (PlanStore Plans, TerrainService Terrain) Create(SettingsStore? settings = null)
    {
        var tiles = new TerrainTileStore(_directory, new HttpClient(_bucket));
        SyntheticGeoTiff.Write(tiles.TilePath(47, 8), 101, 101, 32, 48, 8, 0.01, (row, _) => 400 + row);
        var plans = new PlanStore(new NotificationStore(TimeProvider.System));
        var options = Options.Create(new StationOptions { Terrain = { Enabled = true } });
        return (plans, new TerrainService(plans, options, tiles, TimeProvider.System, NullLogger<TerrainService>.Instance, settings));
    }

    [Fact]
    public async Task By_hand_the_missing_cells_are_listed_until_the_operator_downloads_them()
    {
        var (plans, terrain) = Create();
        using var _ = terrain;

        plans.UpdateSettings(s => s with { PlannedHome = Edge });
        await StationFixture.WaitForAsync(() => terrain.MissingCells.SequenceEqual(["N46E008"]));
        await Task.Delay(200);
        Assert.Equal((TerrainDownloadMode.Manual, 0), (terrain.DownloadMode, _bucket.Requests));

        var result = await terrain.DownloadPlanAreaAsync();

        Assert.Equal(["N46E008"], result!.NotPublished);
        Assert.Equal(["N47E008"], result.Present);
        Assert.Empty(terrain.MissingCells);
        Assert.False(terrain.IsDownloading);
    }

    [Fact]
    public async Task Automatically_a_missing_cell_is_tried_once_as_the_plan_reaches_it()
    {
        var settings = new SettingsStore(new StationPaths(_directory, _directory), NullLogger<SettingsStore>.Instance);
        var (plans, terrain) = Create(settings);
        using var _ = terrain;
        var downloads = new TaskCompletionSource<TileFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        terrain.Downloaded += result => downloads.TrySetResult(result);
        _bucket.Offline = true;

        plans.UpdateSettings(s => s with { PlannedHome = Edge });
        var first = await downloads.Task.WaitAsync(TimeSpan.FromSeconds(10));
        plans.UpdateSettings(s => s with { PlannedHome = new GeoPoint(Edge.Latitude + 0.001, Edge.Longitude) });
        await Task.Delay(1000);

        Assert.Equal(TerrainDownloadMode.Auto, terrain.DownloadMode);
        Assert.Equal("N46E008", Assert.Single(first.Failed).Cell);
        Assert.Equal(1, _bucket.Requests);
        Assert.Equal(["N46E008"], terrain.MissingCells);
        settings.Set(TerrainService.DownloadModeSetting, nameof(TerrainDownloadMode.Manual));
        Assert.Equal(TerrainDownloadMode.Manual, terrain.DownloadMode);
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
        var simulator = new Comms.LinkAndVehicleTests.FakeSimulator();
        var link = new Ketras.Comms.LinkStatus(Ketras.Comms.LinkState.Connected, new Ketras.Comms.LinkProfile { Name = "sim", Kind = Ketras.Comms.LinkKind.Simulator, SimulatorId = "fake" });
        var radio = new Ketras.Comms.LinkStatus(Ketras.Comms.LinkState.Connected, new Ketras.Comms.LinkProfile { Name = "radio", Kind = Ketras.Comms.LinkKind.Serial });

        Assert.False(SimulatorGroundWorker.IsFlat(Ketras.Comms.LinkStatus.Disconnected, [simulator]));
        simulator.Run("flat");
        Assert.True(SimulatorGroundWorker.IsFlat(Ketras.Comms.LinkStatus.Disconnected, [simulator]));
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
        var far = await terrain.PrepareForUploadAsync(new Ketras.Planning.Plan { Mission = MissionEditor.UpdateSettings(new Mission(), s => s with { PlannedHome = new GeoPoint(-30.5, 20.5) }) }, []);
        var empty = await terrain.PrepareForUploadAsync(new Ketras.Planning.Plan(), []);

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

    /// <summary>The bucket has no cell beyond the ones on disk, or cannot be reached; counts requests.</summary>
    private sealed class NotFound : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        public bool Offline { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Offline ? throw new HttpRequestException("offline") : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
