using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Ketras.Controls.Scene3D.Rendering;
using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Model.Terrain;
using Ketras.Theme;

[assembly: AvaloniaTestApplication(typeof(Ketras.Controls.Scene3D.Tests.TestApp))]

namespace Ketras.Controls.Scene3D.Tests;

/// <summary>An application with only the Ketras theme, in the dark variant.</summary>
public sealed class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

    public override void Initialize()
    {
        Styles.Add(new KetrasTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }
}

public sealed class AirspaceViewTests
{
    private static readonly GeoPoint Home = new(47.397971, 8.546164);

    private static void Fill(AirspaceView view)
    {
        view.SetOptions(new SceneOptions { Origin = Home, HomeAltitudeMeters = 488 });
        view.SetOwnship(new SceneAircraft("vehicle-1", "Vehicle 1", GeoMath.Offset(Home, 45, 300), 560, 90, 5, 15, 18, 1, "AUTO.MISSION"));
        view.SetTraffic([new SceneAircraft("adsb:4CA2B1", "EIN123", GeoMath.Offset(Home, 120, 2000), 900, 270)]);
        view.SetVolumes(
        [
            new SceneVolume("area", "Area", new CircleArea(Home, 1500), true, 488, 650),
            new SceneVolume("pad", "Pad", new PolygonArea([GeoMath.Offset(Home, 180, 400), GeoMath.Offset(Home, 200, 500), GeoMath.Offset(Home, 160, 500)]), false, 488, 650)
        ]);
        view.SetPath(
        [
            new ScenePathPoint(2, GeoMath.Offset(Home, 0, 800), 568, "Waypoint", MissionPhase.Cruise),
            new ScenePathPoint(1, GeoMath.Offset(Home, 90, 300), 538, "Takeoff", MissionPhase.Takeoff)
        ]);
        view.SetPoints([new ScenePoint("Mast", GeoMath.Offset(Home, 300, 600))]);
    }

    [AvaloniaFact]
    public async Task Inputs_from_any_thread_reach_the_renderer_mapped_and_ordered()
    {
        var view = new AirspaceView();
        await Task.Run(() => Fill(view));
        Dispatcher.UIThread.RunJobs();

        var (scene, options, followed) = view.Snapshot();
        Assert.Equal(Home, options.Origin);
        Assert.Equal("vehicle-1", scene.Ownship!.VehicleId);
        Assert.Equal(560, scene.Ownship.AltitudeMeters);
        Assert.Equal("EIN123", Assert.Single(scene.Traffic).DisplayName);
        var circle = Assert.IsType<CircularFence>(scene.Fences[0]);
        Assert.Equal((true, 1500.0, 650.0), (circle.Inclusion, circle.RadiusMeters, circle.MaxAltitudeMeters));
        Assert.Equal(3, Assert.IsType<PolygonFence>(scene.Fences[1]).Vertices.Count);
        Assert.Equal([1, 2], scene.MissionPath.Select(p => p.Sequence));
        Assert.Equal(MissionPathPhase.Takeoff, scene.MissionPath[0].Phase);
        Assert.Equal("Mast", Assert.Single(scene.Points).Label);
        Assert.Null(followed);

        view.Follow(" adsb:4CA2B1 ");
        Assert.Equal("adsb:4CA2B1", view.Followed);
        view.FitAll();
        Assert.Null(view.Followed);
        var (eye, target, isFree) = view.Viewport.CameraForTests();
        Assert.True(isFree);
        Assert.True(eye.Y > 500 && (target - eye).Y < -0.99f * (target - eye).Length(), $"eye {eye}, target {target}");
    }

    [AvaloniaFact]
    public void Taking_the_camera_off_a_followed_aircraft_ends_following_without_moving_the_view()
    {
        var view = new AirspaceView();
        Fill(view);
        var window = new Window { Content = view, Width = 800, Height = 500 };
        window.Show();
        view.Follow("adsb:4CA2B1");
        Dispatcher.UIThread.RunJobs();
        var released = 0;
        view.FollowReleased += () => released++;

        var before = view.Viewport.CameraForTests();
        var aircraftBefore = view.Viewport.AircraftForTests("adsb:4CA2B1")!.Value;
        view.Viewport.TakeCamera();
        var after = view.Viewport.CameraForTests();
        var aircraftAfter = view.Viewport.AircraftForTests("adsb:4CA2B1")!.Value;

        // The traffic is 2 km from home: the world origin moves from it to the home, the view must not.
        Assert.True(after.IsFree);
        Assert.True((aircraftAfter - aircraftBefore).Length() > 1000);
        Assert.True(((aircraftAfter - after.Eye) - (aircraftBefore - before.Eye)).Length() < 0.5f);
        Assert.Equal(1, released);
        Assert.Null(view.Followed);
        window.Close();
    }

    [AvaloniaFact]
    public void A_full_scene_lays_out_and_renders_in_software_without_terrain()
    {
        var view = new AirspaceView();
        Fill(view);
        var window = new Window { Content = view, Width = 800, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Equal(new Size(800, 500), view.Bounds.Size);
        using var bitmap = new RenderTargetBitmap(new PixelSize(800, 500));
        bitmap.Render(view);
        view.Follow("vehicle-1");
        Dispatcher.UIThread.RunJobs();
        bitmap.Render(view);
        window.Close();
    }

    [Fact]
    public void The_terrain_mesh_is_cut_into_chunks_with_levels_and_keeps_its_attribution()
    {
        const int rows = 130, columns = 70;
        var heights = new float[rows * columns];
        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = 400 + (i % columns);
        }

        heights[5] = float.NaN;
        var grid = new TerrainGrid(47.3, 8.5, 1.0 / 3600, 1.0 / 3600, rows, columns, heights, "test DEM");

        var mesh = TerrainMesh.Build(grid);

        Assert.Equal("test DEM", mesh.Attribution);
        Assert.Equal(3 * 2, mesh.Chunks.Count); // 129 × 69 cells in 64-cell chunks
        // A full 64-cell chunk has every level of detail; a narrow edge chunk only the strides that fit it.
        Assert.Equal(TerrainMesh.Strides.Length, mesh.Chunks[0].Levels);
        Assert.All(mesh.Chunks, chunk => Assert.InRange(chunk.Levels, 1, TerrainMesh.Strides.Length));
        Assert.True(mesh.CellMeters is > 20 and < 32);
        // A chunk's lowest point includes the skirt that hides cracks between levels.
        Assert.All(mesh.Chunks, chunk => Assert.True(chunk.MinHeight < chunk.MaxHeight));
        Assert.True(mesh.Chunks.Min(chunk => chunk.MinHeight) < 400);
    }
}
