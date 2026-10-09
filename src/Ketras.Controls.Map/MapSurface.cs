using Avalonia.Controls;
using Ketras.Controls.Map.Rendering;
using Ketras.Controls.Map.Telemetry;
using Ketras.Model.Environment;
using Ketras.Model.Fences;
using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.UI.Avalonia;
using Mapsui.Widgets.ScaleBar;

namespace Ketras.Controls.Map;

/// <summary>Facade over Mapsui: owns the map host and turns what it is given into layers; nothing else references Mapsui types.</summary>
internal sealed class MapSurface
{
    /// <summary>Web-Mercator resolution at zoom 0, used to convert zoom levels to resolutions.</summary>
    private const double ResolutionAtZoomZero = 156543.033928;

    /// <summary>Padding applied when framing a set of points, as a fraction of the extent.</summary>
    private const double FitPadding = 0.25;

    private readonly VehicleTrackStore _tracks;
    private readonly MapLayerSet _layers = new();
    private readonly MapControl _mapControl = new();

    public MapSurface(VehicleTrackStore tracks)
    {
        _tracks = tracks;

        // The ground colour shows wherever no tile is drawn, and is the whole background while the base map is off.
        _mapControl.Map = new Mapsui.Map { CRS = "EPSG:3857", BackColor = MapPalette.Ground };
        _mapControl.Map.Navigator.ViewportChanged += (_, _) => RefreshGrid();
        Map.Widgets.Enqueue(new ScaleBarWidget(Map)
        {
            HorizontalAlignment = Mapsui.Widgets.HorizontalAlignment.Center,
            VerticalAlignment = Mapsui.Widgets.VerticalAlignment.Bottom,
            TextColor = MapPalette.ScaleText,
            Halo = MapPalette.SymbolOutline,
            MaxWidth = 130,
            ScaleBarMode = ScaleBarMode.Single,
            Margin = new MRect(0, 8)
        });
    }

    public Control View => _mapControl;

    public Mapsui.Map Map => _mapControl.Map;

    /// <summary>Map units per pixel at the current zoom.</summary>
    public double Resolution => Map.Navigator.Viewport.Resolution;

    /// <summary>The base map's attribution.</summary>
    public string Attribution => _layers.Attribution;

    /// <summary>Rebuilds the layer stack on a base map.</summary>
    public void SetBaseMap(BaseMapKind kind, CustomTileSource? custom, string? cacheDirectory, MapLayers visibility)
    {
        _layers.Rebuild(Map, kind, custom, cacheDirectory);
        ApplyVisibility(visibility);
    }

    public void ApplyVisibility(MapLayers visibility)
    {
        _layers.ApplyVisibility(visibility);
        RefreshGrid();
        Refresh();
    }

    public void RefreshMission(Mission mission, Guid? selectedId, int? executingIndex, IReadOnlyList<GeoPoint> terrainConflicts)
    {
        var features = MissionLayerBuilder.Build(mission, selectedId, executingIndex, Resolution, terrainConflicts);
        _layers.SetMissionFeatures(features.Route, features.Items);
        Refresh();
    }

    public void RefreshFences(IReadOnlyList<Fence> fences, IReadOnlyList<Fence> external, string? selectedId, IReadOnlyList<GeoPoint> draft)
    {
        _layers.SetFenceFeatures(FenceLayerBuilder.Build(fences, external, selectedId, draft, Resolution));
        Refresh();
    }

    public void RefreshGuides(IReadOnlyList<MapGuide> guides)
    {
        _layers.SetGuideFeatures(GuideLayerBuilder.Build(guides));
        Refresh();
    }

    public void RefreshMarkers(IReadOnlyList<PointOfInterest> points)
    {
        _layers.SetMarkerFeatures(MarkerLayerBuilder.Build(points));
        Refresh();
    }

    public void RefreshVehicles()
    {
        var features = VehicleLayerBuilder.Build(_tracks, VehicleLayerOptions.Ownship, VehicleLayerOptions.Traffic, Resolution);
        _layers.SetVehicleFeatures(features.Ownship, features.Traffic, features.OwnshipTrail, features.TrafficTrails);
        Refresh();
    }

    /// <summary>The grid follows the view: only the visible extent, at a spacing that suits the zoom.</summary>
    private void RefreshGrid()
    {
        if (_layers.ShowsGrid)
        {
            _layers.SetGridFeatures(GridLayerBuilder.Build(Map.Navigator.Viewport.ToExtent(), Resolution));
        }
    }

    private void Refresh() => _mapControl.RefreshGraphics();

    public void CenterOn(GeoPoint point, double? zoom = null)
    {
        var center = MapGeometry.ToMercator(point);
        if (zoom is { } level)
        {
            Map.Navigator.CenterOnAndZoomTo(center, ResolutionAtZoomZero / Math.Pow(2, level));
        }
        else
        {
            Map.Navigator.CenterOn(center);
        }
    }

    /// <summary>Frames every point given, with a margin so symbols are not cut off at the edge.</summary>
    public bool FitTo(IReadOnlyList<GeoPoint> points)
    {
        if (points.Count == 0)
        {
            return false;
        }

        if (points.Count == 1)
        {
            CenterOn(points[0]);
            return true;
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var point in points)
        {
            var world = MapGeometry.ToMercator(point);
            minX = Math.Min(minX, world.X);
            minY = Math.Min(minY, world.Y);
            maxX = Math.Max(maxX, world.X);
            maxY = Math.Max(maxY, world.Y);
        }

        var padX = Math.Max((maxX - minX) * FitPadding, 1);
        var padY = Math.Max((maxY - minY) * FitPadding, 1);
        Map.Navigator.ZoomToBox(new MRect(minX - padX, minY - padY, maxX + padX, maxY + padY));
        return true;
    }

    public void CenterOnOwnship()
    {
        if (_tracks.Ownship is { HasFirstFix: true } ownship)
        {
            Map.Navigator.CenterOn(ownship.CurrentWorld);
        }
    }

    /// <summary>Grab radius for hit testing, kept constant on screen across zoom levels.</summary>
    public double GrabThresholdMapUnits(double pixels = 30) => Resolution * pixels;
}
