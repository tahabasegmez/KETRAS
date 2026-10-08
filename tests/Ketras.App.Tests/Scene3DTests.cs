using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ketras.App.Scene3D;
using Ketras.App.Shell;
using Ketras.Controls.Scene3D;
using Ketras.Model.Environment;
using Ketras.Model.Fences;
using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Model.Vehicles;
using Ketras.Station.Environment;
using Ketras.Station.Links;
using Ketras.Station.Missions;
using Ketras.Station.Vehicles;

namespace Ketras.App.Tests;

public sealed class Scene3DTests
{
    private static readonly GeoPoint Home = new(47.397971, 8.546164);

    [Fact]
    public async Task The_mapper_puts_the_plan_on_its_home_datum_and_raises_volumes_above_the_route()
    {
        await using var app = AppFixture.Create();
        var plans = app.Get<PlanStore>();
        plans.UpdateSettings(s => s with { PlannedHome = Home, HomeAltitudeMeters = 488 });
        plans.AddItem(MissionItemType.Takeoff, GeoMath.Offset(Home, 90, 300), new MissionItemOptions { AltitudeMeters = 50 });
        plans.AddItem(MissionItemType.Waypoint, GeoMath.Offset(Home, 0, 900), new MissionItemOptions { AltitudeMeters = 200 });
        plans.AddItem(MissionItemType.ChangeSpeed, null);
        plans.UpsertFence(new Fence("area", new CircleArea(Home, 2000), Inclusion: true));
        var zones = new[]
        {
            new Zone { Id = "z1", Source = "file", Name = "Restricted", Area = new CircleArea(Home, 100), FloorMeters = 500, CeilingMeters = 900 },
            new Zone { Id = "z2", Source = "file", Area = new CircleArea(Home, 100), Kind = ZoneKind.Advisory }
        };
        var plan = plans.Current;

        var options = SceneMapper.Options(plan);
        var path = SceneMapper.Path(plan);
        var volumes = SceneMapper.Volumes(plan, zones);

        Assert.Equal((Home, 488.0), (options.Origin!.Value, options.HomeAltitudeMeters));
        Assert.Equal([(1, 538.0, MissionPhase.Takeoff), (2, 688.0, MissionPhase.Cruise)], path.Select(p => (p.Sequence, p.AltitudeMeters, p.Phase)));
        Assert.Equal(2, volumes.Count);
        Assert.Equal(("area", true, 488.0, 488.0 + 230), (volumes[0].Id, volumes[0].StayIn, volumes[0].FloorMeters, volumes[0].CeilingMeters));
        Assert.Equal(("file:z1", "Restricted", false, 500.0, 900.0), (volumes[1].Id, volumes[1].Label, volumes[1].StayIn, volumes[1].FloorMeters, volumes[1].CeilingMeters));
        Assert.Null(SceneMapper.Options(new Model.Planning.Plan()).Origin);
    }

    [Fact]
    public void Aircraft_map_with_their_altitudes_and_ids()
    {
        var snapshot = new VehicleSnapshot
        {
            Id = new VehicleId(3), Name = "Vehicle 3", Position = Home, AltitudeAmslMeters = 600, HeadingDegrees = 45, Mode = "AUTO.LOITER"
        };

        var ownship = SceneMapper.Ownship(snapshot)!;
        var traffic = Assert.Single(SceneMapper.Traffic(
            [new TrafficTarget { Id = "adsb:1", Source = "ADS-B", Position = Home, Category = TrafficCategory.Light }], 488));

        Assert.Equal(("vehicle-3", 600.0, 45.0, "AUTO.LOITER"), (ownship.Id, ownship.AltitudeMeters, ownship.HeadingDegrees, ownship.Mode));
        Assert.Null(SceneMapper.Ownship(snapshot with { Position = null }));
        Assert.Equal(("adsb:1", 488.0, "Light"), (traffic.Name, traffic.AltitudeMeters, traffic.Mode));
        Assert.Equal("Mast", Assert.Single(SceneMapper.Points([new PointOfInterest { Id = "p", Source = "x", Name = "Mast", Location = Home }])).Label);
    }

    [Fact]
    public async Task The_view_model_lists_traffic_and_follows_the_chosen_aircraft()
    {
        await using var app = AppFixture.Create();
        await app.StartAsync();
        var scene = app.Get<Scene3DViewModel>();
        Assert.False(scene.HasTraffic);

        await app.Get<ConnectionService>().ConnectAsync(new LinkProfile { Name = "Bench" });
        await AppFixture.WaitForAsync(() => app.Get<VehicleRegistry>().Active is { Parameters.IsLoaded: true });
        app.Vehicle.SendAdsb(0x4CA2B1, "EIN123", 47.41, 8.52, 1250, 270, 70);
        app.Vehicle.SendPosition(47.39, 8.54, 520, 30, headingDegrees: 90);
        await AppFixture.WaitForAsync(() => scene.HasTraffic && scene.Ownship?.Position is not null);

        var row = Assert.Single(scene.TrafficRows);
        Assert.Equal(("adsb:4CA2B1", "EIN123", "1"), (row.Id, row.Name, scene.TrafficCount));
        Assert.StartsWith("Light · 270", row.Detail, StringComparison.Ordinal);

        scene.FollowTrafficCommand.Execute(row);
        Assert.Equal("adsb:4CA2B1", scene.Followed);
        scene.FollowOwnshipCommand.Execute(null);
        Assert.Equal("vehicle-1", scene.Followed);
        var fits = 0;
        scene.FitRequested += () => fits++;
        scene.FitCommand.Execute(null);
        Assert.Null(scene.Followed);
        Assert.Equal(1, fits);

        scene.FollowOwnshipCommand.Execute(null);
        scene.ReleaseFollow();
        Assert.Null(scene.Followed);

        app.Get<ManualEnvironmentProvider>().UpsertPoint(new PointOfInterest { Id = "p", Source = "Manual", Location = Home });
        Assert.Single(scene.Points);
    }

    [AvaloniaFact]
    public async Task The_3d_activity_shows_the_scene()
    {
        await using var app = AppFixture.Create();
        var window = new MainWindow { DataContext = app.Get<ShellViewModel>() };
        window.Show();
        app.Get<ShellViewModel>().Show(ShellViewModel.Scene3D);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var scene = window.GetVisualDescendants().OfType<AirspaceView>().Single();
        Assert.True(scene.Bounds.Width > 0);
        window.Close();
    }
}
