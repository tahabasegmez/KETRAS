using BruTile;
using BruTile.Cache;
using BruTile.Predefined;
using BruTile.Web;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Tiling.Layers;

namespace Ketras.Map.Rendering;

/// <summary>Facade over <see cref="Mapsui.Map.Layers"/>: the only type that touches it, owning layer order and contents.</summary>
internal sealed class MapLayerSet
{
    /// <summary>Tile servers ask clients to identify themselves (the OpenStreetMap tile usage policy requires it).</summary>
    private const string UserAgent = "Ketras ground station (https://github.com/ketras)";

    private const string EsriWorldImageryUrl =
        "https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";

    private const string OpenStreetMapUrl = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    // Style = null: every feature carries its own styles, and a layer's default style would paint an opaque
    // white fill and grey outline under each of them.
    private readonly MemoryLayer _grid = new("Grid") { Style = null, Enabled = false };
    private readonly MemoryLayer _fences = new("Fences") { Style = null };
    private readonly MemoryLayer _missionRoute = new("MissionRoute") { Style = null };
    private readonly MemoryLayer _guides = new("Guides") { Style = null };
    private readonly MemoryLayer _missionItems = new("MissionItems") { Style = null };
    private readonly MemoryLayer _markers = new("Markers") { Style = null };
    private readonly MemoryLayer _trafficTrails = new("TrafficTrails") { Style = null };
    private readonly MemoryLayer _ownshipTrail = new("OwnshipTrail") { Style = null };
    private readonly MemoryLayer _traffic = new("Traffic") { Style = null };
    private readonly MemoryLayer _ownship = new("Ownship") { Style = null };

    private ILayer? _baseLayer;

    /// <summary>
    /// Rebuilds the stack with a fresh base layer. Order is bottom-to-top: tiles, the grid that stands in for them,
    /// fences, route, the selected item's guides, items, markers, trails, vehicles - live aircraft always on top.
    /// </summary>
    public void Rebuild(Mapsui.Map map, BaseMapKind kind, CustomTileSource? customSource, string? cacheDirectory)
    {
        _baseLayer = CreateBaseLayer(kind, customSource, cacheDirectory);

        map.Layers.Clear();
        map.Layers.Add(_baseLayer);
        map.Layers.Add(_grid);
        map.Layers.Add(_fences);
        map.Layers.Add(_missionRoute);
        map.Layers.Add(_guides);
        map.Layers.Add(_missionItems);
        map.Layers.Add(_markers);
        map.Layers.Add(_trafficTrails);
        map.Layers.Add(_ownshipTrail);
        map.Layers.Add(_traffic);
        map.Layers.Add(_ownship);
    }

    public void ApplyVisibility(MapLayers visibility)
    {
        if (_baseLayer is not null)
        {
            _baseLayer.Enabled = visibility.BaseMap;
        }

        _grid.Enabled = !visibility.BaseMap;

        _fences.Enabled = visibility.Fences;

        _missionRoute.Enabled = visibility.Mission;
        _missionItems.Enabled = visibility.Mission;
        _guides.Enabled = visibility.Mission && visibility.Guides;

        // Vehicle and trail toggle together; a trail with no aircraft reads as a stale track.
        _ownship.Enabled = visibility.Ownship;
        _ownshipTrail.Enabled = visibility.Ownship;

        _traffic.Enabled = visibility.Traffic;
        _trafficTrails.Enabled = visibility.Traffic;
    }

    public void SetMissionFeatures(IReadOnlyList<IFeature> route, IReadOnlyList<IFeature> items)
    {
        _missionRoute.Features = route;
        _missionItems.Features = items;
    }

    public void SetFenceFeatures(IReadOnlyList<IFeature> features) => _fences.Features = features;

    public void SetGuideFeatures(IReadOnlyList<IFeature> features) => _guides.Features = features;

    /// <summary>True while the guides are drawn; for tests.</summary>
    internal bool ShowsGuides => _guides.Enabled;

    /// <summary>The guide features; for tests.</summary>
    internal IEnumerable<IFeature> GuideFeatures => _guides.Features;

    /// <summary>True while the grid stands in for the base map.</summary>
    public bool ShowsGrid => _grid.Enabled;

    public void SetGridFeatures(IReadOnlyList<IFeature> features) => _grid.Features = features;

    public void SetMarkerFeatures(IReadOnlyList<IFeature> features) => _markers.Features = features;

    public void SetVehicleFeatures(
        IReadOnlyList<IFeature> ownship,
        IReadOnlyList<IFeature> traffic,
        IReadOnlyList<IFeature> ownshipTrail,
        IReadOnlyList<IFeature> trafficTrails)
    {
        _ownship.Features = ownship;
        _traffic.Features = traffic;
        _ownshipTrail.Features = ownshipTrail;
        _trafficTrails.Features = trafficTrails;
    }

    /// <summary>The attribution of the base map in use; empty without one.</summary>
    public string Attribution { get; private set; } = string.Empty;

    /// <summary>
    /// The base map: a custom source when the operator gave one, else Esri World Imagery or OpenStreetMap, each with
    /// the attribution its terms require. Tiles go through a disk cache per source when a directory is given; there
    /// is no bulk download (OpenStreetMap's tile usage policy forbids it).
    /// </summary>
    private ILayer CreateBaseLayer(BaseMapKind kind, CustomTileSource? custom, string? cacheDirectory)
    {
        var (url, name, attribution, minZoom, maxZoom) = custom is not null
            ? (custom.UrlTemplate, "Custom", custom.Attribution, custom.MinZoom, custom.MaxZoom)
            : kind == BaseMapKind.Street
                ? (OpenStreetMapUrl, "Street", "© OpenStreetMap contributors", 0, 19)
                : (EsriWorldImageryUrl, "Satellite", "Esri, Maxar, Earthstar Geographics, and the GIS User Community", 0, 19);
        Attribution = attribution;

        var schema = new GlobalSphericalMercator(YAxis.OSM, minZoom, maxZoom, name);
        var cache = cacheDirectory is null ? null : new FileCache(Path.Combine(cacheDirectory, name.ToLowerInvariant()), "tile");
        var source = new HttpTileSource(schema, new BasicUrlBuilder(url, [], string.Empty), name, cache, new BruTile.Attribution(attribution),
            request => request.Headers.UserAgent.ParseAdd(UserAgent));
        return new TileLayer(source) { Name = name };
    }
}
