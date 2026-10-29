using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using Ketras.Map.Telemetry;
using Ketras.Model.Environment;
using Ketras.Model.Fences;
using Ketras.Model.Geo;
using Ketras.Model.Planning;

namespace Ketras.Map;

/// <summary>
/// Facade: the planning map. It draws a plan (route, items, fences), zones and points of interest that are not
/// editable here, terrain conflicts and live aircraft, on satellite or street tiles (cached on disk) or a dark grid.
/// It never changes the plan: selection is two-way through <see cref="SelectedItemId"/> and
/// <see cref="SelectedFenceId"/>, and every edit goes out as an intent through a command (place, move, reshape, add
/// a fence, pick a point); the host applies it and sets <see cref="Plan"/> back. Aircraft are pushed from any
/// thread and eased at a rate that follows the feed.
/// </summary>
public sealed class MapView : UserControl
{
    /// <summary>Defines <see cref="Plan"/>.</summary>
    public static readonly StyledProperty<Plan> PlanProperty = AvaloniaProperty.Register<MapView, Plan>(nameof(Plan), Plan.Empty);

    /// <summary>Defines <see cref="Zones"/>.</summary>
    public static readonly StyledProperty<IReadOnlyList<Zone>> ZonesProperty = AvaloniaProperty.Register<MapView, IReadOnlyList<Zone>>(nameof(Zones), []);

    /// <summary>Defines <see cref="Points"/>.</summary>
    public static readonly StyledProperty<IReadOnlyList<PointOfInterest>> PointsProperty =
        AvaloniaProperty.Register<MapView, IReadOnlyList<PointOfInterest>>(nameof(Points), []);

    /// <summary>Defines <see cref="TerrainConflicts"/>.</summary>
    public static readonly StyledProperty<IReadOnlyList<GeoPoint>> TerrainConflictsProperty =
        AvaloniaProperty.Register<MapView, IReadOnlyList<GeoPoint>>(nameof(TerrainConflicts), []);

    /// <summary>Defines <see cref="SelectedItemId"/>.</summary>
    public static readonly StyledProperty<Guid?> SelectedItemIdProperty =
        AvaloniaProperty.Register<MapView, Guid?>(nameof(SelectedItemId), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Defines <see cref="SelectedFenceId"/>.</summary>
    public static readonly StyledProperty<string?> SelectedFenceIdProperty =
        AvaloniaProperty.Register<MapView, string?>(nameof(SelectedFenceId), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Defines <see cref="ExecutingItemIndex"/>.</summary>
    public static readonly StyledProperty<int?> ExecutingItemIndexProperty = AvaloniaProperty.Register<MapView, int?>(nameof(ExecutingItemIndex));

    /// <summary>Defines <see cref="Tool"/>.</summary>
    public static readonly StyledProperty<MapTool> ToolProperty =
        AvaloniaProperty.Register<MapView, MapTool>(nameof(Tool), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Defines <see cref="IsEditable"/>.</summary>
    public static readonly StyledProperty<bool> IsEditableProperty = AvaloniaProperty.Register<MapView, bool>(nameof(IsEditable), true);

    /// <summary>Defines <see cref="BaseMap"/>.</summary>
    public static readonly StyledProperty<BaseMapKind> BaseMapProperty = AvaloniaProperty.Register<MapView, BaseMapKind>(nameof(BaseMap));

    /// <summary>Defines <see cref="CustomTiles"/>.</summary>
    public static readonly StyledProperty<CustomTileSource?> CustomTilesProperty = AvaloniaProperty.Register<MapView, CustomTileSource?>(nameof(CustomTiles));

    /// <summary>Defines <see cref="TileCacheDirectory"/>.</summary>
    public static readonly StyledProperty<string?> TileCacheDirectoryProperty = AvaloniaProperty.Register<MapView, string?>(nameof(TileCacheDirectory));

    /// <summary>Defines <see cref="Layers"/>.</summary>
    public static readonly StyledProperty<MapLayers> LayersProperty = AvaloniaProperty.Register<MapView, MapLayers>(nameof(Layers), new MapLayers());

    /// <summary>Defines <see cref="FollowOwnship"/>.</summary>
    public static readonly StyledProperty<bool> FollowOwnshipProperty = AvaloniaProperty.Register<MapView, bool>(nameof(FollowOwnship));

    /// <summary>Defines <see cref="NewFenceRadiusMeters"/>.</summary>
    public static readonly StyledProperty<double> NewFenceRadiusMetersProperty = AvaloniaProperty.Register<MapView, double>(nameof(NewFenceRadiusMeters), 200);

    /// <summary>Defines <see cref="PlaceItemCommand"/>.</summary>
    public static readonly StyledProperty<ICommand?> PlaceItemCommandProperty = AvaloniaProperty.Register<MapView, ICommand?>(nameof(PlaceItemCommand));

    /// <summary>Defines <see cref="MoveItemCommand"/>.</summary>
    public static readonly StyledProperty<ICommand?> MoveItemCommandProperty = AvaloniaProperty.Register<MapView, ICommand?>(nameof(MoveItemCommand));

    /// <summary>Defines <see cref="MoveApproachCommand"/>.</summary>
    public static readonly StyledProperty<ICommand?> MoveApproachCommandProperty = AvaloniaProperty.Register<MapView, ICommand?>(nameof(MoveApproachCommand));

    /// <summary>Defines <see cref="ResizeOrbitCommand"/>.</summary>
    public static readonly StyledProperty<ICommand?> ResizeOrbitCommandProperty = AvaloniaProperty.Register<MapView, ICommand?>(nameof(ResizeOrbitCommand));

    /// <summary>Defines <see cref="Guides"/>.</summary>
    public static readonly StyledProperty<IReadOnlyList<MapGuide>> GuidesProperty = AvaloniaProperty.Register<MapView, IReadOnlyList<MapGuide>>(nameof(Guides), []);

    /// <summary>Defines <see cref="AddFenceCommand"/>.</summary>
    public static readonly StyledProperty<ICommand?> AddFenceCommandProperty = AvaloniaProperty.Register<MapView, ICommand?>(nameof(AddFenceCommand));

    /// <summary>Defines <see cref="EditFenceCommand"/>.</summary>
    public static readonly StyledProperty<ICommand?> EditFenceCommandProperty = AvaloniaProperty.Register<MapView, ICommand?>(nameof(EditFenceCommand));

    /// <summary>Defines <see cref="PickPointCommand"/>.</summary>
    public static readonly StyledProperty<ICommand?> PickPointCommandProperty = AvaloniaProperty.Register<MapView, ICommand?>(nameof(PickPointCommand));

    /// <summary>Defines <see cref="DrawingDiscardedCommand"/>.</summary>
    public static readonly StyledProperty<ICommand?> DrawingDiscardedCommandProperty = AvaloniaProperty.Register<MapView, ICommand?>(nameof(DrawingDiscardedCommand));

    private static readonly TimeSpan InitialFrameInterval = TimeSpan.FromMilliseconds(33);

    private readonly VehicleTrackStore _tracks = new();
    private readonly MapSurface _surface;
    private readonly PointerInteraction _pointer;
    private readonly DispatcherTimer _frameTimer = new(DispatcherPriority.Background) { Interval = InitialFrameInterval };
    private readonly Stopwatch _frameClock = new();
    private readonly List<GeoPoint> _draft = [];
    private string _attribution = string.Empty;

    static MapView()
    {
        PlanProperty.Changed.AddClassHandler<MapView>((view, _) => view.RefreshPlan());
        SelectedItemIdProperty.Changed.AddClassHandler<MapView>((view, _) => view.RefreshMission());
        ExecutingItemIndexProperty.Changed.AddClassHandler<MapView>((view, _) => view.RefreshMission());
        TerrainConflictsProperty.Changed.AddClassHandler<MapView>((view, _) => view.RefreshMission());
        SelectedFenceIdProperty.Changed.AddClassHandler<MapView>((view, _) => view.RefreshFences());
        ZonesProperty.Changed.AddClassHandler<MapView>((view, _) => view.RefreshFences());
        PointsProperty.Changed.AddClassHandler<MapView>((view, _) => view._surface.RefreshMarkers(view.Points));
        GuidesProperty.Changed.AddClassHandler<MapView>((view, _) => view._surface.RefreshGuides(view.Guides));
        ToolProperty.Changed.AddClassHandler<MapView>((view, e) => view.OnToolChanged((MapTool)e.NewValue!));
        BaseMapProperty.Changed.AddClassHandler<MapView>((view, _) => view.RebuildBaseMap());
        CustomTilesProperty.Changed.AddClassHandler<MapView>((view, _) => view.RebuildBaseMap());
        TileCacheDirectoryProperty.Changed.AddClassHandler<MapView>((view, _) => view.RebuildBaseMap());
        LayersProperty.Changed.AddClassHandler<MapView>((view, _) => view._surface.ApplyVisibility(view.Layers));
    }

    /// <summary>An empty map on satellite tiles.</summary>
    public MapView()
    {
        _surface = new MapSurface(_tracks);
        _pointer = new PointerInteraction(_surface, this);
        Content = _surface.View;
        _frameTimer.Tick += (_, _) => OnFrame();
        AttachedToVisualTree += (_, _) =>
        {
            _frameClock.Restart();
            _frameTimer.Start();
        };
        DetachedFromVisualTree += (_, _) => _frameTimer.Stop();
        RebuildBaseMap();
        RefreshPlan();
    }

    /// <summary>The plan drawn: the mission and the operator's fences.</summary>
    public Plan Plan
    {
        get => GetValue(PlanProperty);
        set => SetValue(PlanProperty, value);
    }

    /// <summary>Zones from the environment, drawn under the fences and not editable here (advisory ones are not drawn).</summary>
    public IReadOnlyList<Zone> Zones
    {
        get => GetValue(ZonesProperty);
        set => SetValue(ZonesProperty, value);
    }

    /// <summary>Points of interest, drawn as labelled diamonds.</summary>
    public IReadOnlyList<PointOfInterest> Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>Where the plan meets the terrain, marked on the map.</summary>
    public IReadOnlyList<GeoPoint> TerrainConflicts
    {
        get => GetValue(TerrainConflictsProperty);
        set => SetValue(TerrainConflictsProperty, value);
    }

    /// <summary>The selected mission item; set by a tap or press on its handle.</summary>
    public Guid? SelectedItemId
    {
        get => GetValue(SelectedItemIdProperty);
        set => SetValue(SelectedItemIdProperty, value);
    }

    /// <summary>The selected fence; set by a tap or press on its handle.</summary>
    public string? SelectedFenceId
    {
        get => GetValue(SelectedFenceIdProperty);
        set => SetValue(SelectedFenceIdProperty, value);
    }

    /// <summary>The plan index of the item the vehicle is flying, highlighted; null for none.</summary>
    public int? ExecutingItemIndex
    {
        get => GetValue(ExecutingItemIndexProperty);
        set => SetValue(ExecutingItemIndexProperty, value);
    }

    /// <summary>What a tap does; placing and dropping a circle return to Select by themselves.</summary>
    public MapTool Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    /// <summary>Edits are allowed (point picking always is).</summary>
    public bool IsEditable
    {
        get => GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    /// <summary>The built-in base map.</summary>
    public BaseMapKind BaseMap
    {
        get => GetValue(BaseMapProperty);
        set => SetValue(BaseMapProperty, value);
    }

    /// <summary>A tile source used instead of the built-in ones; null for none.</summary>
    public CustomTileSource? CustomTiles
    {
        get => GetValue(CustomTilesProperty);
        set => SetValue(CustomTilesProperty, value);
    }

    /// <summary>Where tiles are cached on disk, one folder per source; null caches nothing.</summary>
    public string? TileCacheDirectory
    {
        get => GetValue(TileCacheDirectoryProperty);
        set => SetValue(TileCacheDirectoryProperty, value);
    }

    /// <summary>Which layers are drawn.</summary>
    public MapLayers Layers
    {
        get => GetValue(LayersProperty);
        set => SetValue(LayersProperty, value);
    }

    /// <summary>Keeps the view centred on the ownship.</summary>
    public bool FollowOwnship
    {
        get => GetValue(FollowOwnshipProperty);
        set => SetValue(FollowOwnshipProperty, value);
    }

    /// <summary>Radius of a circle dropped with <see cref="MapTool.DrawCircle"/>.</summary>
    public double NewFenceRadiusMeters
    {
        get => GetValue(NewFenceRadiusMetersProperty);
        set => SetValue(NewFenceRadiusMetersProperty, value);
    }

    /// <summary>Place a mission item; the parameter is a <see cref="GeoPoint"/>.</summary>
    public ICommand? PlaceItemCommand
    {
        get => GetValue(PlaceItemCommandProperty);
        set => SetValue(PlaceItemCommandProperty, value);
    }

    /// <summary>Move an item; the parameter is an <see cref="ItemMove"/>, sent on every pointer move of a drag.</summary>
    public ICommand? MoveItemCommand
    {
        get => GetValue(MoveItemCommandProperty);
        set => SetValue(MoveItemCommandProperty, value);
    }

    /// <summary>Reshape a landing approach; the parameter is an <see cref="ApproachMove"/>.</summary>
    public ICommand? MoveApproachCommand
    {
        get => GetValue(MoveApproachCommandProperty);
        set => SetValue(MoveApproachCommandProperty, value);
    }

    /// <summary>Raised with <see cref="OrbitResize"/> while a selected loiter's radius handle is dragged.</summary>
    public ICommand? ResizeOrbitCommand
    {
        get => GetValue(ResizeOrbitCommandProperty);
        set => SetValue(ResizeOrbitCommandProperty, value);
    }

    /// <summary>The selected item's guide drawings (limits around it, what can be dragged); the host computes them.</summary>
    public IReadOnlyList<MapGuide> Guides
    {
        get => GetValue(GuidesProperty);
        set => SetValue(GuidesProperty, value);
    }

    /// <summary>Add a fence; the parameter is its <see cref="Area"/>.</summary>
    public ICommand? AddFenceCommand
    {
        get => GetValue(AddFenceCommandProperty);
        set => SetValue(AddFenceCommandProperty, value);
    }

    /// <summary>Reshape a fence; the parameter is a <see cref="FenceEdit"/>.</summary>
    public ICommand? EditFenceCommand
    {
        get => GetValue(EditFenceCommandProperty);
        set => SetValue(EditFenceCommandProperty, value);
    }

    /// <summary>A point picked with <see cref="MapTool.PickPoint"/>; the parameter is a <see cref="GeoPoint"/>.</summary>
    public ICommand? PickPointCommand
    {
        get => GetValue(PickPointCommandProperty);
        set => SetValue(PickPointCommandProperty, value);
    }

    /// <summary>A polygon closed with fewer than three corners was dropped.</summary>
    public ICommand? DrawingDiscardedCommand
    {
        get => GetValue(DrawingDiscardedCommandProperty);
        set => SetValue(DrawingDiscardedCommandProperty, value);
    }

    /// <summary>The corners of the polygon being drawn.</summary>
    public IReadOnlyList<GeoPoint> Draft => _draft;

    /// <summary>Defines <see cref="Attribution"/>.</summary>
    public static readonly DirectProperty<MapView, string> AttributionProperty =
        AvaloniaProperty.RegisterDirect<MapView, string>(nameof(Attribution), view => view.Attribution);

    /// <summary>The base map's credit, as its licence requires; the host shows it.</summary>
    public string Attribution
    {
        get => _attribution;
        private set => SetAndRaise(AttributionProperty, ref _attribution, value);
    }

    /// <summary>Feeds an aircraft sample; safe from any thread.</summary>
    public void PushAircraft(MapAircraft aircraft) => _tracks.Enqueue(aircraft);

    /// <summary>Drops every aircraft and trail.</summary>
    public void ClearAircraft() => Dispatcher.UIThread.Post(() =>
    {
        _tracks.Reset();
        _surface.RefreshVehicles();
    });

    /// <summary>Moves the view to a point, optionally to a zoom level.</summary>
    public void CenterOn(GeoPoint point, double? zoom = null) => _surface.CenterOn(point, zoom);

    /// <summary>Frames the planned home, the mission and the fences; false when the plan has no position.</summary>
    public bool FitToPlan()
    {
        var plan = Plan;
        var points = new List<GeoPoint>();
        if (!plan.Mission.Settings.PlannedHome.IsUnset)
        {
            points.Add(plan.Mission.Settings.PlannedHome);
        }

        points.AddRange(plan.Mission.Items.Where(i => i.Location is not null).Select(i => i.Location!.Value));
        points.AddRange(plan.Fences.SelectMany(f => f.Area.Points));
        return _surface.FitTo(points);
    }

    /// <summary>Delivers a tap as the pointer would; for tests and keyboard-driven hosts.</summary>
    internal void TapAt(GeoPoint point) => _pointer.Tap(point, Rendering.MapGeometry.ToMercator(point));

    /// <summary>Delivers a right click as the pointer would.</summary>
    internal void RightClick() => _pointer.RightClick();

    internal void Select(Guid? itemId, string? fenceId)
    {
        SelectedItemId = itemId;
        SelectedFenceId = fenceId;
    }

    internal void EndTool()
    {
        _draft.Clear();
        Tool = MapTool.Select;
        RefreshFences();
    }

    internal void AddDraftVertex(GeoPoint point)
    {
        _draft.Add(point);
        RefreshFences();
    }

    /// <summary>Closes the polygon being drawn into a fence; with fewer than three corners it is dropped.</summary>
    internal void FinishPolygon()
    {
        var corners = _draft.ToList();
        EndTool();
        if (corners.Count < 3)
        {
            Execute(DrawingDiscardedCommand, null);
            return;
        }

        RaiseAddFence(new PolygonArea([.. corners]));
    }

    internal void RaisePlaceItem(GeoPoint point) => Execute(PlaceItemCommand, point);

    internal void RaiseMoveItem(ItemMove move) => Execute(MoveItemCommand, move);

    internal void RaiseMoveApproach(ApproachMove move) => Execute(MoveApproachCommand, move);

    internal void RaiseResizeOrbit(OrbitResize resize) => Execute(ResizeOrbitCommand, resize);

    internal void RaiseAddFence(Area area) => Execute(AddFenceCommand, area);

    internal void RaiseEditFence(FenceEdit edit) => Execute(EditFenceCommand, edit);

    internal void RaisePickPoint(GeoPoint point) => Execute(PickPointCommand, point);

    private static void Execute(ICommand? command, object? parameter)
    {
        if (command?.CanExecute(parameter) == true)
        {
            command.Execute(parameter);
        }
    }

    private void OnToolChanged(MapTool tool)
    {
        if (tool != MapTool.DrawPolygon && _draft.Count > 0)
        {
            _draft.Clear();
            RefreshFences();
        }

        _pointer.ToolChanged(tool);
    }

    private void RebuildBaseMap()
    {
        _surface.SetBaseMap(BaseMap, CustomTiles, TileCacheDirectory, Layers);
        Attribution = _surface.Attribution;
        RefreshPlan();
        _surface.RefreshMarkers(Points);
        _surface.RefreshGuides(Guides);
        _surface.RefreshVehicles();
    }

    private void RefreshPlan()
    {
        RefreshMission();
        RefreshFences();
    }

    private void RefreshMission() => _surface.RefreshMission(Plan.Mission, SelectedItemId, ExecutingItemIndex, TerrainConflicts);

    private void RefreshFences()
    {
        var zones = Zones.Where(z => z.Kind != ZoneKind.Advisory).Select(z => new Fence($"{z.Source}:{z.Id}", z.Area, z.Kind == ZoneKind.StayIn)).ToList();
        _surface.RefreshFences(Plan.Fences, zones, SelectedFenceId, _draft);
    }

    private void OnFrame()
    {
        var dt = _frameClock.Elapsed.TotalSeconds;
        _frameClock.Restart();
        var samples = _tracks.DrainPending();
        foreach (var sample in samples)
        {
            _tracks.Apply(sample);
        }

        if (samples.Count > 0 | _tracks.Advance(dt))
        {
            _surface.RefreshVehicles();
            if (FollowOwnship)
            {
                _surface.CenterOnOwnship();
            }
        }

        var recommended = _tracks.RecommendedFrameInterval();
        if (_frameTimer.Interval != recommended)
        {
            _frameTimer.Interval = recommended;
        }
    }
}
