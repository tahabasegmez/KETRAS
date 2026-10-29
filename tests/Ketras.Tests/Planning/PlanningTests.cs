using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ketras.Controls;
using Ketras.Controls.Map;
using Ketras.Model.Fences;
using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Planning;
using Ketras.Resources;
using Ketras.Station.Missions;
using Ketras.Station.Notifications;
using Ketras.Utilities;
using Microsoft.Extensions.DependencyInjection;

namespace Ketras.Tests.Planning;

[Collection(AvaloniaCollection.Name)]
public sealed class PlanningTests
{
    private static readonly GeoPoint Home = new(47.397971, 8.546164);

    private static AppFixture Create(TimeProvider? time = null, string? filePath = null) =>
        AppFixture.Create(time, register: services => services.AddSingleton<IFileDialogService>(new FixedFileDialogs(filePath)));

    [Fact]
    public async Task Picking_a_positioned_type_arms_the_map_and_the_placement_adds_and_selects_it()
    {
        await using var app = Create();
        var planning = app.Get<PlanningViewModel>();
        var loiter = planning.Mission.Groups.SelectMany(g => g.Types).Single(t => t.Type == MissionItemType.LoiterTime);

        planning.Mission.AddItemCommand.Execute(loiter);
        Assert.Equal(MapTool.AddItem, planning.Tool);
        planning.PlaceItemCommand.Execute(Home);

        var item = Assert.Single(app.Get<PlanStore>().Current.Mission.Items);
        Assert.Equal((MissionItemType.LoiterTime, Home), (item.Type, item.Location!.Value));
        Assert.Equal(item.Id, planning.SelectedItemId);
        var row = Assert.Single(planning.Mission.Rows);
        Assert.True(row.IsSelected);
        Assert.Equal(["altitude", "loiter-time", "radius"], row.Fields.Select(f => f.Key));
        Assert.True(row.HasLocation);
    }

    [Fact]
    public async Task A_type_without_a_position_is_added_at_once()
    {
        await using var app = Create();
        var planning = app.Get<PlanningViewModel>();
        var speed = planning.Mission.Groups.SelectMany(g => g.Types).Single(t => t.Type == MissionItemType.ChangeSpeed);

        planning.Mission.AddItemCommand.Execute(speed);

        Assert.Equal(MapTool.Select, planning.Tool);
        Assert.Equal(MissionItemType.ChangeSpeed, Assert.Single(app.Get<PlanStore>().Current.Mission.Items).Type);
        Assert.False(planning.Mission.Rows[0].HasLocation);
    }

    [Fact]
    public async Task A_field_stores_a_value_in_range_and_refuses_one_outside_flashing_each_for_a_second()
    {
        var time = new ManualTime();
        await using var app = Create(time);
        var planning = app.Get<PlanningViewModel>();
        var plans = app.Get<PlanStore>();
        var id = plans.AddItem(MissionItemType.Waypoint, Home)!.Value;
        var altitude = planning.Mission.Rows.Single().Fields.Single(f => f.Key == "altitude");

        altitude.Text = "150";
        altitude.CommitCommand.Execute(null);
        Assert.Equal(150, plans.Current.Mission.Items.Single(i => i.Id == id).AltitudeMeters);
        Assert.True(altitude.IsSaved);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(FieldFlash.None, altitude.Flash);

        altitude.Text = "25000";
        altitude.CommitCommand.Execute(null);
        Assert.True(altitude.IsRejected);
        Assert.NotEmpty(altitude.Problem);
        Assert.Equal("150", altitude.Text);
        Assert.Equal(150, plans.Current.Mission.Items.Single().AltitudeMeters);

        altitude.Text = "abc";
        altitude.CancelCommand.Execute(null);
        Assert.Equal("150", altitude.Text);
    }

    [Fact]
    public async Task The_altitude_reference_converts_the_plan_and_the_landing_settings_go_into_it()
    {
        await using var app = Create();
        var planning = app.Get<PlanningViewModel>();
        var plans = app.Get<PlanStore>();
        plans.UpdateSettings(s => s with { PlannedHome = Home, HomeAltitudeMeters = 488 });
        plans.AddItem(MissionItemType.Waypoint, Home, new MissionItemOptions { AltitudeMeters = 100 });
        var mission = planning.Mission;
        Assert.All(new[] { mission.AltitudeReference, mission.LandingTerrain, mission.AbortIfNotFound, mission.AbortIfLost }, field => Assert.True(field.HasHelp));

        mission.AltitudeReference.SelectedChoice = mission.AltitudeReference.Choices.Single(c => c.Value == (int)AltitudeReference.AboveSeaLevel);
        Assert.Equal((AltitudeReference.AboveSeaLevel, 588), (plans.Current.Mission.Settings.AltitudeReference, plans.Current.Mission.Items[0].AltitudeMeters));
        Assert.Equal("588 m AMSL", mission.Rows[0].Summary);

        // No terrain data in the tests: above terrain is refused and the plan stays as it was.
        mission.AltitudeReference.SelectedChoice = mission.AltitudeReference.Choices.Single(c => c.Value == (int)AltitudeReference.AboveTerrain);
        Assert.Equal(AltitudeReference.AboveSeaLevel, plans.Current.Mission.Settings.AltitudeReference);
        Assert.True(mission.AltitudeReference.IsRejected);
        Assert.Contains(app.Get<NotificationStore>().History, n => n.Title == "The altitude reference was not changed" && n.Detail == "Item 1 has no terrain data under it.");

        mission.AbortIfLost.SelectedChoice = mission.AbortIfLost.Choices.Single(c => c.Value == 1);
        mission.LandingTerrain.SelectedChoice = mission.LandingTerrain.Choices.Single(c => c.Value == (int)LandingTerrainUse.GlideSlope);
        Assert.Equal(new LandingSettings { TerrainUse = LandingTerrainUse.GlideSlope, AbortIfTerrainLost = true }, plans.Current.Mission.Settings.Landing);
    }

    [Fact]
    public async Task Moving_an_item_on_the_map_updates_the_row()
    {
        await using var app = Create();
        var planning = app.Get<PlanningViewModel>();
        var id = app.Get<PlanStore>().AddItem(MissionItemType.Waypoint, Home)!.Value;
        var moved = GeoMath.Offset(Home, 90, 500);

        planning.MoveItemCommand.Execute(new ItemMove(id, moved));

        var row = planning.Mission.Rows.Single();
        Assert.Equal(moved, row.Item.Location);
        Assert.Equal(moved.Latitude, row.Latitude!.Text is { Length: > 0 } ? double.Parse(row.Latitude.Text, System.Globalization.CultureInfo.CurrentCulture) : double.NaN, 6);
    }

    [Fact]
    public async Task A_drawn_fence_is_a_keep_out_zone_and_its_row_edits_it()
    {
        await using var app = Create();
        var planning = app.Get<PlanningViewModel>();
        var plans = app.Get<PlanStore>();

        planning.Fences.DrawCircleCommand.Execute(null);
        Assert.Equal(MapTool.DrawCircle, planning.Tool);
        planning.AddFenceCommand.Execute(new CircleArea(Home, 300));

        Assert.True(planning.IsGeofencePanel);
        var fence = Assert.Single(plans.Current.Fences);
        Assert.False(fence.Inclusion);
        var row = Assert.Single(planning.Fences.Rows);
        Assert.Equal("keep out · circle 300 m", row.Summary);

        row.Kind.SelectedChoice = row.Kind.Choices.Single(c => c.Label == "Stay in");
        Assert.True(plans.Current.Fences.Single().Inclusion);
        Assert.True(row.IsStayIn);
        Assert.StartsWith("stay in", row.Summary, StringComparison.Ordinal);

        row.Radius!.Text = "450";
        row.Radius.CommitCommand.Execute(null);
        Assert.Equal(450, ((CircleArea)plans.Current.Fences.Single().Area).RadiusMeters);

        planning.EditFenceCommand.Execute(new FenceEdit(fence.Id, new CircleArea(GeoMath.Offset(Home, 0, 100), 450)));
        Assert.Equal(GeoMath.Offset(Home, 0, 100), ((CircleArea)plans.Current.Fences.Single().Area).Center);

        row.DeleteCommand.Execute(null);
        Assert.Empty(plans.Current.Fences);
        Assert.True(planning.Fences.IsEmpty);
    }

    [Fact]
    public async Task Selecting_an_item_clears_the_fence_selection_and_back()
    {
        await using var app = Create();
        var planning = app.Get<PlanningViewModel>();
        var plans = app.Get<PlanStore>();
        var id = plans.AddItem(MissionItemType.Waypoint, Home)!.Value;
        plans.UpsertFence(new Fence("F1", new CircleArea(Home, 200)));

        planning.Fences.Rows.Single().SelectCommand.Execute(null);
        Assert.Equal(("F1", (Guid?)null), (planning.SelectedFenceId, planning.SelectedItemId));
        Assert.True(planning.Fences.Rows.Single().IsSelected);

        planning.Mission.Rows.Single().SelectCommand.Execute(null);
        Assert.Equal((null, (Guid?)id), (planning.SelectedFenceId, planning.SelectedItemId));
        Assert.False(planning.Fences.Rows.Single().IsSelected);

        planning.Mission.Rows.Single().SelectCommand.Execute(null);
        Assert.Null(planning.SelectedItemId);

        planning.Mission.Rows.Single().SelectCommand.Execute(null);
        plans.RemoveItem(id);
        Assert.Null(planning.SelectedItemId);
    }

    [Fact]
    public async Task The_phase_and_fence_tools_light_up_while_armed_and_a_second_press_disarms()
    {
        await using var app = Create();
        var planning = app.Get<PlanningViewModel>();
        var cruise = planning.Mission.Groups.Single(g => g.Phase == MissionPhase.Cruise);

        planning.Mission.AddItemCommand.Execute(cruise.Types.Single(t => t.Type == MissionItemType.Waypoint));
        Assert.True(cruise.IsArmed);
        Assert.False(planning.Mission.Groups.Single(g => g.Phase == MissionPhase.Takeoff).IsArmed);

        planning.ShowGeofencePanelCommand.Execute(null);
        Assert.False(cruise.IsArmed);
        planning.Fences.DrawPolygonCommand.Execute(null);
        Assert.True(planning.Fences.IsPolygonArmed);
        planning.Fences.DrawPolygonCommand.Execute(null);
        Assert.Equal(MapTool.Select, planning.Tool);
        Assert.False(planning.Fences.IsPolygonArmed);
    }

    [Fact]
    public async Task A_discarded_polygon_is_reported()
    {
        await using var app = Create();
        var planning = app.Get<PlanningViewModel>();

        planning.DrawingDiscardedCommand.Execute(null);

        Assert.Contains(app.Get<NotificationStore>().History, n => n.Source == PlanStore.Source && n.Title == "Polygon discarded");
    }

    [Fact]
    public async Task The_base_map_switches_and_is_remembered()
    {
        await using var app = Create();
        var planning = app.Get<PlanningViewModel>();
        Assert.True(planning.IsSatellite);

        planning.SwitchBaseMapCommand.Execute(null);
        Assert.Equal(BaseMapKind.Street, planning.BaseMap);

        planning.ToggleMissionLayerCommand.Execute(null);
        Assert.False(planning.Layers.Mission);
        Assert.True(planning.Layers.BaseMap);

        var again = ActivatorUtilities.CreateInstance<PlanningViewModel>(app.Services);
        Assert.Equal(BaseMapKind.Street, again.BaseMap);
        again.Dispose();
    }

    [Fact]
    public async Task A_custom_tile_source_from_settings_replaces_the_base_maps()
    {
        await using var app = Create();
        var settings = app.Get<Ketras.Settings.SettingsViewModel>();
        var planning = app.Get<PlanningViewModel>();

        settings.TileUrl = "not a url";
        settings.CommitTilesCommand.Execute(null);
        Assert.NotEmpty(settings.TileProblem);
        Assert.Null(planning.CustomTiles);

        settings.TileUrl = "https://tiles.example.org/{z}/{x}/{y}.png";
        settings.TileAttribution = "© Example";
        settings.CommitTilesCommand.Execute(null);

        Assert.Empty(settings.TileProblem);
        Assert.Equal(new CustomTileSource("https://tiles.example.org/{z}/{x}/{y}.png", "© Example"), planning.CustomTiles);
        Assert.False(planning.CanSwitchBaseMap);

        settings.TileUrl = string.Empty;
        settings.CommitTilesCommand.Execute(null);
        Assert.Null(planning.CustomTiles);
    }

    [Fact]
    public async Task A_plan_saves_to_and_opens_from_a_plan_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ketras-{Guid.NewGuid():N}.plan");
        try
        {
            await using var app = Create(filePath: path);
            var planning = app.Get<PlanningViewModel>();
            var plans = app.Get<PlanStore>();
            plans.UpdateSettings(s => s with { PlannedHome = Home });
            plans.AddItem(MissionItemType.Takeoff, Home);
            plans.AddItem(MissionItemType.Waypoint, GeoMath.Offset(Home, 0, 800));
            plans.UpsertFence(new Fence("F1", new CircleArea(Home, 1500), true));

            await planning.SavePlanCommand.ExecuteAsync(null);
            Assert.True(File.Exists(path));
            plans.ClearMission();
            plans.ClearFences();

            await planning.OpenPlanCommand.ExecuteAsync(null);

            Assert.Equal([MissionItemType.Takeoff, MissionItemType.Waypoint], plans.Current.Mission.Items.Select(i => i.Type));
            Assert.True(Assert.Single(plans.Current.Fences).Inclusion);
            Assert.Equal(2, planning.Mission.Rows.Count);
            var history = app.Get<NotificationStore>().History;
            Assert.Contains(history, n => n.Title == "Plan saved");
            Assert.Contains(history, n => n.Title == "Plan opened");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_broken_plan_file_is_reported_and_the_plan_kept()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ketras-{Guid.NewGuid():N}.plan");
        await File.WriteAllTextAsync(path, "{ not json");
        try
        {
            await using var app = Create(filePath: path);
            var planning = app.Get<PlanningViewModel>();
            var plans = app.Get<PlanStore>();
            plans.AddItem(MissionItemType.Waypoint, Home);

            await planning.OpenPlanCommand.ExecuteAsync(null);

            Assert.Single(plans.Current.Mission.Items);
            Assert.Contains(app.Get<NotificationStore>().History, n => n.Title == "Plan not opened" && n.Severity == NotificationSeverity.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task The_view_shows_the_map_the_panel_and_the_selected_rows_fields()
    {
        await using var app = Create();
        var planning = app.Get<PlanningViewModel>();
        var window = new Window { Width = 1400, Height = 900, Content = new PlanningView { DataContext = planning } };
        window.Show();
        Pump(window);

        Assert.Single(window.GetVisualDescendants().OfType<MapView>());
        Assert.True(Find<Control>(window, "MissionPanel").IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "FencePanel").IsEffectivelyVisible);

        var id = app.Get<PlanStore>().AddItem(MissionItemType.Waypoint, Home)!.Value;
        planning.SelectItem(id);
        Pump(window);
        Assert.Contains(window.GetVisualDescendants().OfType<FieldRowView>(),
            v => v.DataContext is FieldViewModel { Key: "altitude" } && v.IsEffectivelyVisible);

        Find<Button>(window, "GeofenceTab").Command!.Execute(null);
        Pump(window);
        Assert.True(Find<Control>(window, "FencePanel").IsEffectivelyVisible);

        var switchBaseMap = Find<Button>(window, "SwitchBaseMap");
        switchBaseMap.Command!.Execute(null);
        Pump(window);
        Assert.Equal(BaseMapKind.Street, window.GetVisualDescendants().OfType<MapView>().Single().BaseMap);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_panel_collapses_to_its_switch_and_the_settings_have_no_home_and_narrow_choices()
    {
        await using var app = Create();
        var planning = app.Get<PlanningViewModel>();
        var window = new Window { Width = 1400, Height = 900, Content = new PlanningView { DataContext = planning } };
        window.Show();
        Pump(window);

        var fields = window.GetVisualDescendants().OfType<FieldRowView>().Select(v => v.DataContext).OfType<FieldViewModel>().ToList();
        Assert.DoesNotContain(fields, f => f.Key.StartsWith("home-", StringComparison.Ordinal));
        Assert.All(fields, f => Assert.Equal(110, f.EditorWidth));
        Assert.Equal("RTL", planning.Mission.EndAction.SelectedChoice!.Label);

        // The clear action sits on the MISSION ITEMS section.
        var clear = Find<Button>(window, "ClearMission");
        Assert.Equal(Strings.PlanningMissionItems, clear.FindAncestorOfType<CollapsibleSection>()!.Title);

        var panel = Find<Control>(window, "PlanningPanel");
        var expanded = panel.Bounds.Height;
        Find<Button>(window, "TogglePanel").Command!.Execute(null);
        Pump(window);
        Assert.False(planning.IsPanelExpanded);
        Assert.False(Find<Control>(window, "PlanningContent").IsVisible);
        Assert.True(panel.Bounds.Height < 60 && expanded > 200, $"{panel.Bounds.Height} / {expanded}");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pressing_a_phase_opens_its_strip_and_releasing_on_a_tile_picks_that_type()
    {
        await using var app = Create();
        var planning = app.Get<PlanningViewModel>();
        var window = new Window { Width = 1400, Height = 900, Content = new PlanningView { DataContext = planning } };
        window.Show();
        Pump(window);
        var picker = window.GetVisualDescendants().OfType<PhasePicker>().Single();
        var cruiseFace = picker.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("phase-face")).ElementAt(1);
        var press = cruiseFace.TranslatePoint(new Point(cruiseFace.Bounds.Width / 2, cruiseFace.Bounds.Height / 2), window)!.Value;

        // Released where it was pressed: the strip opened under the pointer, so that is its first tile.
        window.MouseDown(press, MouseButton.Left);
        Pump(window);
        window.MouseMove(press + new Point(1, 0));
        Pump(window);
        window.MouseUp(press + new Point(1, 0), MouseButton.Left);
        Pump(window);

        var first = planning.Mission.Groups.Single(g => g.Phase == MissionPhase.Cruise).Types[0];
        Assert.Equal(MapTool.AddItem, planning.Tool);
        Assert.True(planning.Mission.Groups.Single(g => g.Phase == MissionPhase.Cruise).IsArmed);
        planning.PlaceItemCommand.Execute(Home);
        Assert.Equal(first.Type, Assert.Single(app.Get<PlanStore>().Current.Mission.Items).Type);

        // Released away from the strip: nothing is picked.
        planning.Tool = MapTool.Select;
        window.MouseDown(press, MouseButton.Left);
        Pump(window);
        window.MouseMove(press + new Point(0, 300));
        window.MouseUp(press + new Point(0, 300), MouseButton.Left);
        Pump(window);
        Assert.Equal(MapTool.Select, planning.Tool);
        window.Close();
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(control => control.Name == name);

    [Fact]
    public async Task A_field_shows_the_span_the_vehicle_allows_and_warns_on_a_value_outside_it_but_refuses_one_outside_its_limits()
    {
        await using var app = Create();
        var planning = app.Get<PlanningViewModel>();
        var plans = app.Get<PlanStore>();
        plans.AddItem(MissionItemType.Takeoff, GeoMath.Offset(Home, 0, 300));
        var id = plans.AddItem(MissionItemType.LoiterUnlimited, GeoMath.Offset(Home, 0, 2000))!.Value;
        planning.SelectItem(id);
        var radius = planning.Mission.Rows.Single(r => r.Id == id).Fields.Single(f => f.Key == "radius");

        // PX4 defaults: 18 m/s at 50° bank turns in about 27.7 m.
        Assert.Contains("Turn ≥ 28 m at 18 m/s", radius.RangeLine, StringComparison.Ordinal);
        Assert.Contains("Accepted 10–5000 m", radius.RangeTip, StringComparison.Ordinal);
        Assert.Empty(radius.Advice);

        radius.Text = "20";
        radius.CommitCommand.Execute(null);
        Assert.Equal(FieldFlash.Warned, radius.Flash);
        Assert.Equal(20, MissionItemValues.LoiterRadiusMeters(plans.Current.Mission.Items[1]));
        Assert.Equal(radius.RangeLine, radius.Advice);
        Assert.True(radius.ShowsAdvice);

        radius.Text = "5";
        radius.CommitCommand.Execute(null);
        Assert.Equal(FieldFlash.Rejected, radius.Flash);
        Assert.Equal(20, MissionItemValues.LoiterRadiusMeters(plans.Current.Mission.Items[1]));
    }

    [Fact]
    public async Task The_end_action_shows_only_while_the_plan_has_no_landing()
    {
        await using var app = Create();
        var planning = app.Get<PlanningViewModel>();
        var plans = app.Get<PlanStore>();
        plans.AddItem(MissionItemType.Takeoff, GeoMath.Offset(Home, 0, 300));
        Assert.True(planning.Mission.ShowEndAction);

        var land = plans.AddItem(MissionItemType.Land, GeoMath.Offset(Home, 180, 600))!.Value;
        Assert.False(planning.Mission.ShowEndAction);

        plans.RemoveItem(land);
        Assert.True(planning.Mission.ShowEndAction);
    }

    [Fact]
    public async Task The_selected_item_gets_its_guides_and_a_dragged_radius_handle_resizes_its_orbit()
    {
        await using var app = Create();
        var planning = app.Get<PlanningViewModel>();
        var plans = app.Get<PlanStore>();
        plans.AddItem(MissionItemType.Takeoff, GeoMath.Offset(Home, 0, 300));
        var id = plans.AddItem(MissionItemType.LoiterUnlimited, GeoMath.Offset(Home, 0, 2000))!.Value;
        Assert.Empty(planning.Guides);

        planning.SelectItem(id);
        planning.ResizeOrbitCommand.Execute(new OrbitResize(id, 140));

        Assert.Contains(planning.Guides, g => g.Shape == MapGuideShape.Ring && g.Label.StartsWith("min orbit", StringComparison.Ordinal));
        Assert.Contains(planning.Guides, g => g.Shape == MapGuideShape.Handle && g.Label == "drag: radius 140 m");
        Assert.Equal(140, MissionItemValues.LoiterRadiusMeters(plans.Current.Mission.Items[1]));
        planning.ToggleGuidesLayerCommand.Execute(null);
        Assert.False(planning.Layers.Guides);
        planning.SelectItem(null);
        Assert.Empty(planning.Guides);
    }

    [AvaloniaFact]
    public void The_span_line_shows_under_a_field_only_while_its_box_has_the_focus()
    {
        var field = new FieldViewModel("radius", "Radius", "m", 80, 0, 10, 5000, _ => true, new InlineUiDispatcher(), TimeProvider.System);
        field.SetRange(new ParameterRange(10, 5000, 28, null, RangeReason.TurnRadius, 18), "tip", "Turn ≥ 28 m at 18 m/s");
        var other = new FieldViewModel("delay", "Delay", "s", 0, 0, 0, 3600, _ => true, new InlineUiDispatcher(), TimeProvider.System);
        var row = new FieldRowView { DataContext = field };
        var window = new Window { Width = 300, Height = 200, Content = new StackPanel { Children = { row, new FieldRowView { DataContext = other } } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var line = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RangeLine");
        Assert.False(line.IsVisible);

        row.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "Box").Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(field.IsEditing);
        Assert.True(line.IsVisible);
        Assert.Equal("Turn ≥ 28 m at 18 m/s", line.Text);
        Assert.False(other.IsEditing);
        window.Close();
    }
}
