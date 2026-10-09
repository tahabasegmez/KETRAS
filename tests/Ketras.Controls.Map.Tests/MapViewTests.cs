using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Ketras.Model.Fences;
using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Model.Planning;
using Ketras.Theme;

[assembly: AvaloniaTestApplication(typeof(Ketras.Controls.Map.Tests.TestApp))]

namespace Ketras.Controls.Map.Tests;

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

/// <summary>Records what a map intent carried.</summary>
internal sealed class Recorder : ICommand
{
    public List<object?> Calls { get; } = [];

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => Calls.Add(parameter);
}

public sealed class MapViewTests
{
    private static readonly GeoPoint Home = new(47.397971, 8.546164);

    private static (MapView Map, Window Window) Show(Plan? plan = null)
    {
        var map = new MapView { Layers = new MapLayers { BaseMap = false }, Plan = plan ?? Plan.Empty };
        var window = new Window { Width = 800, Height = 600, Content = map };
        window.Show();
        Pump(window);
        return (map, window);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void A_tap_with_the_place_tool_raises_one_placement_and_ends_the_tool()
    {
        var (map, window) = Show();
        var place = new Recorder();
        map.PlaceItemCommand = place;
        map.Tool = MapTool.AddItem;

        map.TapAt(Home);
        map.TapAt(GeoMath.Offset(Home, 90, 100));
        Pump(window);

        Assert.Equal([Home], place.Calls.Cast<GeoPoint>());
        Assert.Equal(MapTool.Select, map.Tool);
    }

    [AvaloniaFact]
    public void A_circle_is_dropped_with_the_new_fence_radius()
    {
        var (map, _) = Show();
        var add = new Recorder();
        map.AddFenceCommand = add;
        map.NewFenceRadiusMeters = 350;
        map.Tool = MapTool.DrawCircle;

        map.TapAt(Home);

        var circle = Assert.IsType<CircleArea>(Assert.Single(add.Calls));
        Assert.Equal(Home, circle.Center);
        Assert.Equal(350, circle.RadiusMeters);
        Assert.Equal(MapTool.Select, map.Tool);
    }

    [AvaloniaFact]
    public void A_polygon_closes_on_right_click_with_its_corners()
    {
        var (map, _) = Show();
        var add = new Recorder();
        map.AddFenceCommand = add;
        map.Tool = MapTool.DrawPolygon;
        GeoPoint[] corners = [Home, GeoMath.Offset(Home, 0, 300), GeoMath.Offset(Home, 90, 300)];

        foreach (var corner in corners)
        {
            map.TapAt(corner);
        }

        Assert.Empty(add.Calls);
        map.RightClick();

        var polygon = Assert.IsType<PolygonArea>(Assert.Single(add.Calls));
        Assert.Equal(corners, polygon.Vertices);
        Assert.Equal(MapTool.Select, map.Tool);
    }

    [AvaloniaFact]
    public void A_polygon_with_two_corners_is_discarded()
    {
        var (map, _) = Show();
        var add = new Recorder();
        var discarded = new Recorder();
        map.AddFenceCommand = add;
        map.DrawingDiscardedCommand = discarded;
        map.Tool = MapTool.DrawPolygon;

        map.TapAt(Home);
        map.TapAt(GeoMath.Offset(Home, 0, 300));
        map.RightClick();

        Assert.Empty(add.Calls);
        Assert.Single(discarded.Calls);
    }

    [AvaloniaFact]
    public void Nothing_is_raised_while_the_map_is_read_only()
    {
        var (map, _) = Show();
        var place = new Recorder();
        map.PlaceItemCommand = place;
        map.IsEditable = false;
        map.Tool = MapTool.AddItem;

        map.TapAt(Home);

        Assert.Empty(place.Calls);
    }

    [AvaloniaFact]
    public void Picking_a_point_works_on_a_read_only_map()
    {
        var (map, _) = Show();
        var pick = new Recorder();
        map.PickPointCommand = pick;
        map.IsEditable = false;
        map.Tool = MapTool.PickPoint;

        map.TapAt(Home);

        Assert.Equal([Home], pick.Calls.Cast<GeoPoint>());
    }

    [AvaloniaFact]
    public void A_tap_on_an_item_selects_it_and_a_tap_on_nothing_clears_it()
    {
        var waypoint = MissionItemFactory.Create(MissionItemType.Waypoint, GeoMath.Offset(Home, 45, 500));
        var plan = new Plan { Mission = new Mission { Items = [waypoint] } };
        var (map, window) = Show(plan);
        map.CenterOn(waypoint.Location!.Value, 15);
        Pump(window);

        map.TapAt(waypoint.Location!.Value);
        Assert.Equal(waypoint.Id, map.SelectedItemId);

        map.TapAt(GeoMath.Offset(Home, 225, 3000));
        Assert.Null(map.SelectedItemId);
    }

    [AvaloniaFact]
    public void A_tap_on_a_fence_handle_selects_it()
    {
        var plan = new Plan { Fences = [new Fence("F1", new CircleArea(Home, 400))] };
        var (map, window) = Show(plan);
        map.CenterOn(Home, 14);
        Pump(window);

        map.TapAt(GeoMath.Offset(Home, 90, 400));

        Assert.Equal("F1", map.SelectedFenceId);
        Assert.Null(map.SelectedItemId);
    }

    [AvaloniaFact]
    public void Aircraft_and_a_full_plan_draw_without_errors()
    {
        var plan = new Plan
        {
            Mission = new Mission
            {
                Items =
                [
                    MissionItemFactory.Create(MissionItemType.Takeoff, Home),
                    MissionItemFactory.Create(MissionItemType.Waypoint, GeoMath.Offset(Home, 0, 800)),
                    MissionItemFactory.Create(MissionItemType.LoiterTime, GeoMath.Offset(Home, 90, 800)),
                    MissionItemFactory.Create(MissionItemType.Land, GeoMath.Offset(Home, 180, 300))
                ]
            },
            Fences = [new Fence("F1", new PolygonArea([Home, GeoMath.Offset(Home, 0, 2000), GeoMath.Offset(Home, 90, 2000)]), true)]
        };
        var (map, window) = Show(plan);

        map.PushAircraft(new MapAircraft("1", "Vehicle 1", true, Home, 500, 90, 18, "AUTO.MISSION"));
        map.PushAircraft(new MapAircraft("adsb:1", "EIN1", false, GeoMath.Offset(Home, 45, 1000), 900, 270, 60, "Light"));
        map.ExecutingItemIndex = 1;
        Assert.True(map.FitToPlan());
        Pump(window);
        using var bitmap = new RenderTargetBitmap(new PixelSize(800, 600));
        bitmap.Render(map);
    }

    [Fact]
    public void A_selected_loiter_offers_its_radius_handle_and_no_other_item_does()
    {
        var loiter = MissionItemFactory.Create(MissionItemType.LoiterUnlimited, Home) with { Param3 = 150 };
        var waypoint = MissionItemFactory.Create(MissionItemType.Waypoint, GeoMath.Offset(Home, 0, 2000));
        var center = Rendering.MapGeometry.ToMercator(Home);
        var handle = Interaction.MapHitTester.OrbitRadiusHandle(center, 150, Home.Latitude);
        var threshold = Rendering.MapGeometry.MetersToMapUnits(10, Home.Latitude);

        var selected = Interaction.MapHitTester.FindMissionHandle([loiter, waypoint], handle, threshold, loiter.Id);
        var unselected = Interaction.MapHitTester.FindMissionHandle([loiter, waypoint], handle, threshold, waypoint.Id);

        Assert.Equal((loiter.Id, Interaction.MissionHandleKind.OrbitRadius), (selected!.Item.Id, selected.Kind));
        Assert.Null(unselected);
        Assert.Equal(150, Rendering.MapGeometry.DistanceBetween(center, handle) * Math.Cos(Home.Latitude * Math.PI / 180), 1);
    }

    [Fact]
    public void Guides_become_rings_with_labels_and_labelled_points_and_hide_with_their_layer()
    {
        var guides = new[]
        {
            new MapGuide(MapGuideShape.Ring, Home, 60, "min turn 60 m", false),
            new MapGuide(MapGuideShape.Mark, GeoMath.Offset(Home, 0, 300), 0, "120 m reached", true),
            new MapGuide(MapGuideShape.Handle, GeoMath.Offset(Home, 90, 150), 0, "drag: radius 150 m", false)
        };
        var layers = new Rendering.MapLayerSet();

        var features = Rendering.GuideLayerBuilder.Build(guides);
        layers.SetGuideFeatures(features);
        layers.ApplyVisibility(new MapLayers());
        var shown = layers.ShowsGuides;
        layers.ApplyVisibility(new MapLayers { Guides = false });

        Assert.Equal(4, features.Count);
        Assert.Equal(["min turn 60 m", "120 m reached", "drag: radius 150 m"],
            features.SelectMany(f => f.Styles.OfType<Mapsui.Styles.LabelStyle>().Select(l => l.GetLabelText(f))));
        Assert.True(shown);
        Assert.False(layers.ShowsGuides);
    }
}
