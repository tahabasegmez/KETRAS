using Avalonia.Input;
using Avalonia.Interactivity;
using Ketras.Controls.Map.Interaction;
using Ketras.Controls.Map.Rendering;
using Ketras.Model.Geo;
using Mapsui;
using Mapsui.Manipulations;
using Mapsui.UI.Avalonia;

namespace Ketras.Controls.Map;

/// <summary>
/// State machine: turns pointer events into selection and edit intents. One drag state covers mission and fence
/// handles; the plan itself is never changed here, the intents go out through <see cref="MapView"/>'s commands and
/// the plan comes back through its properties.
/// </summary>
internal sealed class PointerInteraction
{
    private readonly MapSurface _surface;
    private readonly MapView _view;
    private readonly MapControl _mapControl;

    private MissionDragTarget? _missionDrag;
    private FenceDragTarget? _fenceDrag;
    private DragOffset _dragOffset;
    private bool _panLockBeforeDrag;
    private bool _rightPressConsumed;

    public PointerInteraction(MapSurface surface, MapView view)
    {
        _surface = surface;
        _view = view;
        _mapControl = (MapControl)surface.View;
        _mapControl.MapTapped += OnMapTapped;
        _mapControl.MapPointerPressed += OnPointerPressed;
        _mapControl.MapPointerMoved += OnPointerMoved;
        _mapControl.MapPointerReleased += OnPointerReleased;
        // Tunnelling, so a right click that ends a tool never reaches Mapsui as a tap.
        _mapControl.AddHandler(InputElement.PointerPressedEvent, OnRightPressed, RoutingStrategies.Tunnel);
        _mapControl.AddHandler(InputElement.PointerReleasedEvent, OnRightReleased, RoutingStrategies.Tunnel);
    }

    /// <summary>Shows the cross cursor for any tool but Select.</summary>
    public void ToolChanged(MapTool tool) => _mapControl.Cursor = tool == MapTool.Select ? null : new Cursor(StandardCursorType.Cross);

    /// <summary>A tap at a geographic point, as a test or the view's own handlers would deliver it.</summary>
    internal void Tap(GeoPoint geo, MPoint world)
    {
        if (_view.Tool == MapTool.PickPoint)
        {
            _view.EndTool();
            _view.RaisePickPoint(geo);
            return;
        }

        if (!_view.IsEditable)
        {
            return;
        }

        switch (_view.Tool)
        {
            // One placement per arming, so the next press can grab and drag what was placed.
            case MapTool.AddItem:
                _view.EndTool();
                _view.RaisePlaceItem(geo);
                break;

            case MapTool.DrawPolygon:
                _view.AddDraftVertex(geo);
                break;

            case MapTool.DrawCircle:
                _view.EndTool();
                _view.RaiseAddFence(new CircleArea(geo, _view.NewFenceRadiusMeters));
                break;

            default:
                SelectNearest(world);
                break;
        }
    }

    /// <summary>A right click: closes a polygon being drawn, ends any other tool.</summary>
    internal void RightClick()
    {
        if (_view.Tool == MapTool.DrawPolygon)
        {
            _view.FinishPolygon();
        }
        else
        {
            _view.EndTool();
        }
    }

    private void OnRightPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_mapControl).Properties.IsRightButtonPressed || _view.Tool is MapTool.Select or MapTool.PickPoint)
        {
            return;
        }

        RightClick();
        _rightPressConsumed = true;
        e.Handled = true;
    }

    private void OnRightReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_rightPressConsumed && e.InitialPressMouseButton == MouseButton.Right)
        {
            _rightPressConsumed = false;
            e.Handled = true;
        }
    }

    private void OnMapTapped(object? sender, MapEventArgs e) => Tap(MapGeometry.ToWgs84(e.WorldPosition), e.WorldPosition);

    private void OnPointerPressed(object? sender, MapEventArgs e)
    {
        if (!_view.IsEditable || _view.Tool != MapTool.Select)
        {
            return;
        }

        var threshold = _surface.GrabThresholdMapUnits();
        _missionDrag = MapHitTester.FindMissionHandle(_view.Plan.Mission.Items, e.WorldPosition, threshold, _view.SelectedItemId);
        // A handled press is not reported as a tap, so pressing a handle also selects its owner.
        if (_missionDrag is not null)
        {
            BeginDrag(HandleWorld(_missionDrag), e.WorldPosition);
            _view.Select(_missionDrag.Item.Id, null);
            e.Handled = true;
            return;
        }

        _fenceDrag = MapHitTester.FindFenceHandle(_view.Plan.Fences, e.WorldPosition, threshold);
        if (_fenceDrag is not null)
        {
            BeginDrag(HandleWorld(_fenceDrag), e.WorldPosition);
            _view.Select(null, _fenceDrag.Fence.Id);
            e.Handled = true;
        }
    }

    // Handled while dragging, so the map does not also pan under the pointer. A hover means the button came up
    // where no release reached the map (e.g. over a panel), so the drag ends instead of following the cursor.
    private void OnPointerMoved(object? sender, MapEventArgs e)
    {
        if ((_missionDrag is not null || _fenceDrag is not null) && e.GestureType == GestureType.Hover)
        {
            EndDrag();
            return;
        }

        if (_missionDrag is not null)
        {
            ApplyMissionDrag(_missionDrag, e.WorldPosition);
            e.Handled = true;
        }
        else if (_fenceDrag is not null)
        {
            ApplyFenceDrag(_fenceDrag, e.WorldPosition);
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, MapEventArgs e)
    {
        if (_missionDrag is null && _fenceDrag is null)
        {
            return;
        }

        e.Handled = true;
        if (_missionDrag is not null)
        {
            ApplyMissionDrag(_missionDrag, e.WorldPosition);
        }
        else if (_fenceDrag is not null)
        {
            ApplyFenceDrag(_fenceDrag, e.WorldPosition);
        }

        EndDrag();
    }

    private void BeginDrag(MPoint anchor, MPoint pointer)
    {
        _dragOffset = DragOffset.Between(anchor, pointer);
        _panLockBeforeDrag = _surface.Map.Navigator.PanLock;
        _surface.Map.Navigator.PanLock = true;
    }

    private void EndDrag()
    {
        _missionDrag = null;
        _fenceDrag = null;
        _surface.Map.Navigator.PanLock = _panLockBeforeDrag;
    }

    private static MPoint HandleWorld(MissionDragTarget target)
    {
        var location = target.Item.Location ?? default;
        var anchor = MapGeometry.ToMercator(location);
        if (target.Kind == MissionHandleKind.OrbitRadius)
        {
            return MapHitTester.OrbitRadiusHandle(anchor, Model.Missions.MissionItemValues.LoiterRadiusMeters(target.Item) ?? 0, location.Latitude);
        }

        if (target.Kind != MissionHandleKind.LandingApproachStart)
        {
            return anchor;
        }

        var approach = MapGeometry.MetersToMapUnits(Math.Max(target.Item.Param3, 1), location.Latitude);
        return LandingPatternGeometry.ApproachStart(anchor, target.Item.Param4, approach);
    }

    private static MPoint HandleWorld(FenceDragTarget target) => target switch
    {
        { Fence.Area: CircleArea circle, Kind: FenceHandleKind.CircleRadius } =>
            new MPoint(MapGeometry.ToMercator(circle.Center).X + MapGeometry.MetersToMapUnits(circle.RadiusMeters, circle.Center.Latitude),
                MapGeometry.ToMercator(circle.Center).Y),
        { Fence.Area: CircleArea circle } => MapGeometry.ToMercator(circle.Center),
        { Fence.Area: PolygonArea polygon, VertexIndex: >= 0 } => MapGeometry.ToMercator(polygon.Vertices[target.VertexIndex]),
        _ => new MPoint()
    };

    private void ApplyMissionDrag(MissionDragTarget target, MPoint pointer)
    {
        var world = _dragOffset.Apply(pointer);
        if (target.Kind == MissionHandleKind.Item)
        {
            _view.RaiseMoveItem(new ItemMove(target.Item.Id, MapGeometry.ToWgs84(world)));
            return;
        }

        // Moving the approach start keeps the touchdown fixed; heading and length follow the drag. The radius handle
        // keeps the orbit's centre.
        if (_view.Plan.Mission.Items.FirstOrDefault(i => i.Id == target.Item.Id)?.Location is not { } touchdown)
        {
            return;
        }

        if (target.Kind == MissionHandleKind.OrbitRadius)
        {
            var radius = MapGeometry.DistanceBetween(MapGeometry.ToMercator(touchdown), world) * Math.Cos(touchdown.Latitude * Math.PI / 180d);
            _view.RaiseResizeOrbit(new OrbitResize(target.Item.Id, Math.Max(Math.Round(radius), 1)));
            return;
        }

        var anchor = MapGeometry.ToMercator(touchdown);
        var length = Math.Max(MapGeometry.DistanceBetween(anchor, world) * Math.Cos(touchdown.Latitude * Math.PI / 180d), 1);
        _view.RaiseMoveApproach(new ApproachMove(target.Item.Id, length, MapGeometry.HeadingBetween(world, anchor)));
    }

    private void ApplyFenceDrag(FenceDragTarget target, MPoint pointer)
    {
        var world = _dragOffset.Apply(pointer);
        var geo = MapGeometry.ToWgs84(world);
        // The fence as it stands now: earlier moves of this drag have come back through the plan.
        var fence = _view.Plan.Fences.FirstOrDefault(f => f.Id == target.Fence.Id) ?? target.Fence;
        Area? area = (fence.Area, target.Kind) switch
        {
            (CircleArea circle, FenceHandleKind.CircleCenter) => circle with { Center = geo },
            (CircleArea circle, FenceHandleKind.CircleRadius) => circle with
            {
                RadiusMeters = Math.Max(MapGeometry.DistanceBetween(MapGeometry.ToMercator(circle.Center), world) * Math.Cos(circle.Center.Latitude * Math.PI / 180d), 1)
            },
            (PolygonArea polygon, FenceHandleKind.PolygonVertex) when target.VertexIndex < polygon.Vertices.Length =>
                new PolygonArea(polygon.Vertices.SetItem(target.VertexIndex, geo)),
            _ => null
        };

        if (area is not null)
        {
            _view.RaiseEditFence(new FenceEdit(fence.Id, area));
        }
    }

    private void SelectNearest(MPoint world)
    {
        var threshold = _surface.GrabThresholdMapUnits();
        if (MapHitTester.FindMissionHandle(_view.Plan.Mission.Items, world, threshold, _view.SelectedItemId) is { } item)
        {
            _view.Select(item.Item.Id, null);
            return;
        }

        _view.Select(null, MapHitTester.FindFenceHandle(_view.Plan.Fences, world, threshold)?.Fence.Id);
    }
}
