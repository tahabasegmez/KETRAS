using Ketras.Planning.Missions;
using Mapsui.Styles;

using MBrush = Mapsui.Styles.Brush;
using MColor = Mapsui.Styles.Color;
using MPen = Mapsui.Styles.Pen;

namespace Ketras.Map.Rendering;

/// <summary>Factory: builds Mapsui styles. Layer builders decide what is on the map; this decides how it looks.</summary>
internal static class MapStyles
{
    /// <summary>Map label font: the UI family (docs/UI_RULES.md), as a single name for the map renderer.</summary>
    private const string LabelFamily = "Segoe UI";

    /// <summary>Readout font for vehicle labels, so the numbers keep their width as they change.</summary>
    private const string MonoFamily = "Cascadia Mono";

    // Symbol sizes are Mapsui scales of its 32 px default symbol: a waypoint disc of 18 px (radius 9), the
    // selection ring around it of 28 px (radius 14), as in the design catalog's map.
    private const double WaypointScale = 18 / 32d;
    private const double SelectionScale = 28 / 32d;
    private const double ExecutionScale = 40 / 32d;
    private const double ConflictScale = 22 / 32d;
    private const double ChipScale = 10 / 32d;
    private const double MarkerScale = 14 / 32d;

    private const float SymbolOutlineWidth = 1.5f;
    private const float LegWidth = 2.5f;
    private const float LegGlowWidth = 7f;
    private const float FenceWidth = 1.5f;

    // ---------------------------------------------------------------- generic

    // Mapsui strokes a LineString with Line and a Polygon's border with Outline, so every stroke sets both.

    public static VectorStyle Stroke(MColor color, float width) => new()
    {
        Line = new MPen(color, width),
        Outline = new MPen(color, width)
    };

    public static VectorStyle FilledOutline(MColor fill, MColor stroke, float width) => new()
    {
        Fill = new MBrush(fill),
        Line = new MPen(stroke, width),
        Outline = new MPen(stroke, width)
    };

    /// <summary>Outline-only ring; the transparent fill keeps the interior clickable-through.</summary>
    public static VectorStyle Ring(MColor stroke, float width) => new()
    {
        Fill = new MBrush(new MColor(0, 0, 0, 0)),
        Line = new MPen(stroke, width),
        Outline = new MPen(stroke, width)
    };

    /// <summary>
    /// Wide translucent stroke drawn beneath a shape. Satellite imagery is high-contrast and
    /// busy; a halo is what keeps a thin symbol from disappearing into it.
    /// </summary>
    public static VectorStyle Glow(MColor color, float width, double opacity = 0.28) =>
        Stroke(MapPalette.Fade(color, opacity), width);

    public static VectorStyle Silhouette(MColor fill, float outlineWidth = 1.3f) =>
        FilledOutline(fill, MapPalette.SymbolOutline, outlineWidth);

    public static LabelStyle MapLabel(string text, MColor? background = null, Offset? offset = null) => new()
    {
        Text = text,
        ForeColor = MapPalette.LabelText,
        BackColor = new MBrush(background ?? MapPalette.LabelBackground),
        BorderColor = MapPalette.LabelBorder,
        BorderThickness = 1,
        CornerRounding = 2,
        Font = new Font { FontFamily = LabelFamily, Size = 11 },
        Offset = offset ?? new Offset(0, 0)
    };

    // ---------------------------------------------------------------- guides

    /// <summary>A guide ring: thin and dashed, so it reads as a limit rather than part of the plan.</summary>
    public static VectorStyle GuideRing(bool warning)
    {
        var color = warning ? MapPalette.GuideWarning : MapPalette.Fade(MapPalette.Guide, 0.75);
        return new VectorStyle
        {
            Fill = new MBrush(new MColor(0, 0, 0, 0)),
            Line = new MPen(color, 1.2f) { PenStyle = PenStyle.Dash },
            Outline = new MPen(color, 1.2f) { PenStyle = PenStyle.Dash }
        };
    }

    /// <summary>A guide point: a small dot on the route, or a hollow square on a drag handle.</summary>
    public static SymbolStyle GuidePoint(bool handle, bool warning) => new()
    {
        SymbolType = handle ? SymbolType.Rectangle : SymbolType.Ellipse,
        Fill = new MBrush(handle ? new MColor(0, 0, 0, 0) : warning ? MapPalette.GuideWarning : MapPalette.Guide),
        Outline = new MPen(warning ? MapPalette.GuideWarning : MapPalette.Guide, 1.5f),
        SymbolScale = (handle ? 11 : 7) / 32d
    };

    /// <summary>A guide's label, in the Warning colour when its limit is broken.</summary>
    public static LabelStyle GuideLabel(string text, bool warning, Offset offset)
    {
        var label = MapLabel(text, offset: offset);
        label.ForeColor = warning ? MapPalette.GuideWarning : MapPalette.LabelText;
        label.Font = new Font { FontFamily = LabelFamily, Size = 10 };
        return label;
    }

    // ---------------------------------------------------------------- mission

    /// <summary>Filled disc behind a waypoint's sequence number, coloured by flight phase; selection adds a ring, the disc keeps its size.</summary>
    public static SymbolStyle WaypointBadge(MissionItemType type) => new()
    {
        SymbolType = SymbolType.Ellipse,
        Fill = new MBrush(MapPalette.ForItem(type)),
        Outline = new MPen(MapPalette.SymbolOutline, SymbolOutlineWidth),
        SymbolScale = WaypointScale
    };

    /// <summary>Disc under the planned-home letter.</summary>
    public static SymbolStyle HomeBadge() => new()
    {
        SymbolType = SymbolType.Ellipse,
        Fill = new MBrush(MapPalette.PreFlight),
        Outline = new MPen(MapPalette.SymbolOutline, SymbolOutlineWidth),
        SymbolScale = WaypointScale
    };

    /// <summary>The sequence number (or the home letter) itself, dark and bold on top of the badge.</summary>
    public static LabelStyle WaypointNumber(int sequence) => BadgeText(sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static LabelStyle BadgeText(string text) => new()
    {
        Text = text,
        ForeColor = MapPalette.SymbolOutline,
        BackColor = new MBrush(new MColor(0, 0, 0, 0)),
        Font = new Font { FontFamily = LabelFamily, Size = 11, Bold = true },
        Offset = new Offset(0, 0)
    };

    /// <summary>Halo that marks the selected item without moving it.</summary>
    public static SymbolStyle SelectionHalo() => new()
    {
        SymbolType = SymbolType.Ellipse,
        Fill = new MBrush(new MColor(0, 0, 0, 0)),
        Outline = new MPen(MapPalette.Selection, SymbolOutlineWidth),
        SymbolScale = SelectionScale
    };

    /// <summary>Halo that marks the item the mission is currently executing.</summary>
    public static SymbolStyle ExecutionHalo() => new()
    {
        SymbolType = SymbolType.Ellipse,
        Fill = new MBrush(MapPalette.Fade(MapPalette.Executing, 0.18)),
        Outline = new MPen(MapPalette.Executing, SymbolOutlineWidth),
        SymbolScale = ExecutionScale
    };

    /// <summary>Ring on a point where the planned path meets terrain.</summary>
    public static SymbolStyle TerrainConflict() => new()
    {
        SymbolType = SymbolType.Ellipse,
        Fill = new MBrush(MapPalette.Fade(MapPalette.TerrainConflict, 0.35)),
        Outline = new MPen(MapPalette.TerrainConflict, 2f),
        SymbolScale = ConflictScale
    };

    /// <summary>Route leg. Legs are coloured by the phase of the item they lead into.</summary>
    public static VectorStyle RouteLeg(MissionPhase phase) =>
        Stroke(MapPalette.ForPhase(phase), LegWidth);

    public static VectorStyle RouteGlow(MissionPhase phase) =>
        Glow(MapPalette.ForPhase(phase), LegGlowWidth, 0.25);

    /// <summary>Chevron on a leg: dark, so it reads on the leg's own colour.</summary>
    public static VectorStyle LegChevron() =>
        Stroke(MapPalette.SymbolOutline, 1.5f);

    /// <summary>Turn arrow and centreline on loiter and landing geometry, in the phase colour.</summary>
    public static VectorStyle DirectionArrow(MissionPhase phase) =>
        Stroke(MapPalette.ForPhase(phase), 1.2f);

    /// <summary>
    /// Small marker for an item that has no location of its own - a speed change, a camera
    /// action, a delay. Drawn on the leg where it takes effect.
    /// </summary>
    public static SymbolStyle ActionChip(bool isSelected) => new()
    {
        SymbolType = SymbolType.Ellipse,
        Fill = new MBrush(isSelected ? MapPalette.Selection : MapPalette.ActionChip),
        Outline = new MPen(MapPalette.SymbolOutline, 1.2f),
        SymbolScale = ChipScale
    };

    public static VectorStyle LoiterRing(MissionItemType type, bool isSelected) =>
        Ring(MapPalette.ForItem(type), isSelected ? 1.8f : 1.2f);

    /// <summary>Translucent approach funnel drawn under a landing pattern.</summary>
    public static VectorStyle ApproachFunnel() =>
        FilledOutline(MapPalette.Fade(MapPalette.Landing, 0.16), MapPalette.Landing, 0.7f);

    // ---------------------------------------------------------------- fences

    /// <summary>
    /// Stay-in boundary: a dashed outline at 80 % over a 6 % fill. Keep-out area: a solid outline at 90 % over a
    /// 10 % fill. A selected fence keeps its fill and dash and takes the selection colour on its outline.
    /// </summary>
    public static VectorStyle FenceArea(bool inclusion, bool isSelected)
    {
        var color = MapPalette.ForFence(inclusion);
        var outline = isSelected ? MapPalette.Selection : MapPalette.Fade(color, inclusion ? 0.8 : 0.9);
        return FenceStyle(MapPalette.Fade(color, inclusion ? 0.06 : 0.1), outline, FenceWidth, inclusion ? PenStyle.Dash : PenStyle.Solid);
    }

    /// <summary>A fence not editable here (e.g. an environment zone): same meaning colours, a thin dotted outline and a lighter fill so it never reads as editable.</summary>
    public static VectorStyle ExternalFenceArea(bool inclusion)
    {
        var color = MapPalette.ForFence(inclusion);
        return FenceStyle(MapPalette.Fade(color, 0.04), MapPalette.Fade(color, 0.7), 1f, PenStyle.Dot);
    }

    private static VectorStyle FenceStyle(MColor fill, MColor outline, float width, PenStyle pen) => new()
    {
        Fill = new MBrush(fill),
        Line = new MPen(outline, width) { PenStyle = pen },
        Outline = new MPen(outline, width) { PenStyle = pen }
    };

    public static VectorStyle FenceOutline(bool inclusion, bool isSelected) =>
        Stroke(isSelected ? MapPalette.Selection : MapPalette.ForFence(inclusion), 1.1f);

    public static VectorStyle FenceDraft() => Stroke(MapPalette.FenceDraft, 0.9f);

    public static VectorStyle FenceHandle() =>
        FilledOutline(MColor.White, MapPalette.SymbolOutline, 1.2f);

    // ---------------------------------------------------------------- background

    public static VectorStyle GridLine(bool major) =>
        Stroke(major ? MapPalette.GridMajor : MapPalette.Grid, major ? 1.4f : 1f);

    // ---------------------------------------------------------------- markers

    /// <summary>Diamond for a point of interest, distinct from round waypoint badges.</summary>
    public static SymbolStyle MapMarker(MColor color) => new()
    {
        SymbolType = SymbolType.Rectangle,
        SymbolRotation = 45,
        Fill = new MBrush(color),
        Outline = new MPen(MapPalette.SymbolOutline, SymbolOutlineWidth),
        SymbolScale = MarkerScale
    };

    // ---------------------------------------------------------------- vehicles

    /// <summary>
    /// Vehicle marker. An explicit image source wins over the built-in symbols, which is how
    /// a host swaps in its own airframe artwork.
    /// </summary>
    public static IStyle VehicleMarker(VehicleLayerOptions options, double heading, MColor? forcedColor = null)
    {
        var symbolType = options.MarkerKind switch
        {
            VehicleMarkerKind.Circle => SymbolType.Ellipse,
            _ => SymbolType.Triangle
        };

        return new SymbolStyle
        {
            SymbolType = symbolType,
            Fill = new MBrush(forcedColor ?? MapPalette.Parse(options.MarkerColorHex, MapPalette.VehicleFallback)),
            Outline = new MPen(MapPalette.SymbolOutline, 1),
            SymbolRotation = heading,
            SymbolScale = Math.Max(0.4, options.MarkerSize / 40d)
        };
    }

    /// <summary>Vehicle readout: Mono 11 in an Overlay box at 90 % with a Border frame, to the right of the marker.</summary>
    public static LabelStyle VehicleLabel(string text, double offsetX) => new()
    {
        Text = text,
        ForeColor = MapPalette.LabelText,
        BackColor = new MBrush(MapPalette.LabelBackground),
        BorderColor = MapPalette.LabelBorder,
        BorderThickness = 1,
        CornerRounding = 2,
        Font = new Font { FontFamily = MonoFamily, Size = 11 },
        HorizontalAlignment = LabelStyle.HorizontalAlignmentEnum.Left,
        Offset = new Offset(offsetX, 0)
    };

    /// <summary>Arrow marker: the vehicle's fill with a dark outline.</summary>
    public static VectorStyle VehicleArrow(MColor fill) => FilledOutline(fill, MapPalette.SymbolOutline, 1.2f);

    public static VectorStyle VehicleTrail(VehicleLayerOptions options, MColor? forcedColor) =>
        Stroke(forcedColor ?? MapPalette.Parse(options.TrailColorHex, MapPalette.VehicleFallback), options.TrailWidth);
}
