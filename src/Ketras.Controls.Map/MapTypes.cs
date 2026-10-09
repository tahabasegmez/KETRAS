using Ketras.Model.Geo;

namespace Ketras.Controls.Map;

/// <summary>What a press or tap on the map does.</summary>
public enum MapTool
{
    /// <summary>Select and drag items and fences.</summary>
    Select,

    /// <summary>The next tap places a mission item (<see cref="MapView.PlaceItemCommand"/>), then the tool returns to Select.</summary>
    AddItem,

    /// <summary>Taps add polygon corners; a right click closes the polygon (<see cref="MapView.AddFenceCommand"/>).</summary>
    DrawPolygon,

    /// <summary>The next tap drops a circle of <see cref="MapView.NewFenceRadiusMeters"/>.</summary>
    DrawCircle,

    /// <summary>The next tap is reported through <see cref="MapView.PickPointCommand"/>, in any mode.</summary>
    PickPoint
}

/// <summary>The built-in base maps.</summary>
public enum BaseMapKind
{
    /// <summary>Esri World Imagery.</summary>
    Satellite,

    /// <summary>OpenStreetMap.</summary>
    Street
}

/// <summary>A tile source the operator supplies, e.g. one they are licensed for.</summary>
/// <param name="UrlTemplate">XYZ template with {x}, {y} and {z}.</param>
/// <param name="Attribution">Text the source's licence requires.</param>
/// <param name="MinZoom">Lowest zoom it serves.</param>
/// <param name="MaxZoom">Highest zoom it serves.</param>
public sealed record CustomTileSource(string UrlTemplate, string Attribution, int MinZoom = 0, int MaxZoom = 19);

/// <summary>An orbit's radius dragged by its handle; the centre stays.</summary>
/// <param name="ItemId">The loiter.</param>
/// <param name="RadiusMeters">New radius.</param>
public sealed record OrbitResize(Guid ItemId, double RadiusMeters);

/// <summary>How a guide drawing is drawn.</summary>
public enum MapGuideShape
{
    /// <summary>A thin dashed circle with its label at the top.</summary>
    Ring,

    /// <summary>A small dot on the route with its label beside it.</summary>
    Mark,

    /// <summary>A hollow square on a drag handle with its label beside it.</summary>
    Handle
}

/// <summary>One drawing of the guide layer, which the host computes for the selected item.</summary>
/// <param name="Shape">How it is drawn.</param>
/// <param name="Position">Its point, or a ring's centre.</param>
/// <param name="RadiusMeters">A ring's radius; 0 otherwise.</param>
/// <param name="Label">Its text, e.g. "min turn 41 m".</param>
/// <param name="Warning">The plan breaks this limit: drawn in the Warning colour.</param>
public sealed record MapGuide(MapGuideShape Shape, GeoPoint Position, double RadiusMeters, string Label, bool Warning);

/// <summary>Which map layers are drawn.</summary>
public sealed record MapLayers
{
    /// <summary>Tiles; without them a metric grid shows.</summary>
    public bool BaseMap { get; init; } = true;

    /// <summary>Fences and zones.</summary>
    public bool Fences { get; init; } = true;

    /// <summary>The mission route and items.</summary>
    public bool Mission { get; init; } = true;

    /// <summary>The ownship and its trail.</summary>
    public bool Ownship { get; init; } = true;

    /// <summary>Other aircraft and their trails.</summary>
    public bool Traffic { get; init; } = true;

    /// <summary>The selected item's guide drawings (shown with the mission).</summary>
    public bool Guides { get; init; } = true;
}

/// <summary>An aircraft sample for the map. Fed from any thread; motion between samples is eased.</summary>
/// <param name="Id">Stable identity; the ownship's is ignored (there is one ownship).</param>
/// <param name="Name">Label.</param>
/// <param name="IsOwnship">The station's own vehicle.</param>
/// <param name="Position">Where it is.</param>
/// <param name="AltitudeMeters">Altitude shown in its label.</param>
/// <param name="HeadingDegrees">Heading, 0…360.</param>
/// <param name="GroundSpeedMps">Ground speed.</param>
/// <param name="Mode">Mode or category text shown in its label.</param>
/// <param name="SampleAge">How old the sample already was when pushed; drives the stale cue.</param>
public sealed record MapAircraft(
    string Id,
    string Name,
    bool IsOwnship,
    GeoPoint Position,
    double AltitudeMeters,
    double HeadingDegrees,
    double GroundSpeedMps = 0,
    string Mode = "",
    TimeSpan? SampleAge = null);

/// <summary>An item dragged to a new position.</summary>
/// <param name="ItemId">The item.</param>
/// <param name="Location">Where it was dropped.</param>
public sealed record ItemMove(Guid ItemId, GeoPoint Location);

/// <summary>A landing pattern's approach swung and stretched by its approach-start handle; the touchdown stays.</summary>
/// <param name="ItemId">The landing pattern.</param>
/// <param name="LengthMeters">New approach length.</param>
/// <param name="HeadingDegrees">New approach heading.</param>
public sealed record ApproachMove(Guid ItemId, double LengthMeters, double HeadingDegrees);

/// <summary>A fence reshaped on the map.</summary>
/// <param name="FenceId">The fence.</param>
/// <param name="Area">Its new area.</param>
public sealed record FenceEdit(string FenceId, Area Area);
