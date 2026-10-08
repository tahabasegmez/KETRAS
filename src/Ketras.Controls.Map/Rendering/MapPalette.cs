using System.Globalization;
using Ketras.Model.Missions;
using MColor = Mapsui.Styles.Color;

namespace Ketras.Controls.Map.Rendering;

/// <summary>
/// Every colour the map draws with, the map side of docs/UI_RULES.md's tokens. Mission items are coloured by flight
/// phase, so the plan reads as a sequence at a glance: climb out green, cruise blue, come down orange.
/// </summary>
internal static class MapPalette
{
    // Flight phases
    public static readonly MColor PreFlight = new(0x9D, 0x9D, 0x9D);
    public static readonly MColor Takeoff = new(0x89, 0xD1, 0x85);
    public static readonly MColor Cruise = new(0x37, 0x94, 0xFF);
    public static readonly MColor Landing = new(0xD1, 0x86, 0x16);

    /// <summary>Non-navigational items (speed, camera, conditions) drawn as chips on a leg.</summary>
    public static readonly MColor ActionChip = new(0xCC, 0xCC, 0xCC);

    /// <summary>Selection keeps an item's own colour and adds this ring or outline.</summary>
    public static readonly MColor Selection = new(0xFF, 0xFF, 0xFF);

    /// <summary>Marks the mission item the vehicle is currently executing, distinct from selection.</summary>
    public static readonly MColor Executing = new(0xB1, 0x80, 0xD7);

    /// <summary>Where the planned path meets terrain.</summary>
    public static readonly MColor TerrainConflict = new(0xF1, 0x4C, 0x4C);

    // Fences
    public static readonly MColor FenceInclusion = new(0x37, 0x94, 0xFF);
    public static readonly MColor FenceExclusion = new(0xF1, 0x4C, 0x4C);
    public static readonly MColor FenceDraft = new(0xCC, 0xA7, 0x00);

    // Chrome
    public static readonly MColor SymbolOutline = new(0x10, 0x10, 0x10);
    public static readonly MColor LabelText = new(0xCC, 0xCC, 0xCC);
    public static readonly MColor LabelBorder = new(0x2B, 0x2B, 0x2B);
    public static readonly MColor LabelBackground = new(0x20, 0x20, 0x20, 0xE6);
    public static readonly MColor LabelBackgroundStrong = new(0x18, 0x18, 0x18, 0xF2);
    public static readonly MColor VehicleFallback = new(0x4D, 0xAA, 0xFC);
    public static readonly MColor PointOfInterest = new(0xCC, 0xA7, 0x00);
    public static readonly MColor ScaleText = new(0xCC, 0xCC, 0xCC);

    // Plain background when the base map is off
    public static readonly MColor Ground = new(0x1B, 0x1D, 0x1F);
    public static readonly MColor Grid = new(0x24, 0x27, 0x2A);
    public static readonly MColor GridMajor = new(0x2C, 0x30, 0x34);

    /// <summary>
    /// Cycled through as traffic appear, so two aircraft are never the same colour until
    /// the list runs out.
    /// </summary>
    private static readonly MColor[] TrafficColors =
    [
        new(0xD7, 0xBA, 0x7D),
        new(0xF4, 0x87, 0x71),
        new(0xC5, 0x86, 0xC0),
        new(0x4E, 0xC9, 0xB0),
        new(0xCE, 0x91, 0x78),
        new(0x9C, 0xDC, 0xFE)
    ];

    public static MColor TrafficColor(int index) => TrafficColors[Math.Abs(index) % TrafficColors.Length];

    /// <summary>Colour a mission item is drawn in.</summary>
    public static MColor ForItem(MissionItemType type) => ForPhase(MissionItemCatalog.PhaseOf(type));

    public static MColor ForPhase(MissionPhase phase) => phase switch
    {
        MissionPhase.PreFlight => PreFlight,
        MissionPhase.Takeoff => Takeoff,
        MissionPhase.Landing => Landing,
        _ => Cruise
    };

    public static MColor ForFence(bool inclusion) => inclusion ? FenceInclusion : FenceExclusion;

    /// <summary>Same hue at a lower alpha, for glows and fills under an outline.</summary>
    public static MColor Fade(MColor color, double opacity) =>
        new(color.R, color.G, color.B, (int)Math.Clamp(opacity * 255d, 0, 255));

    /// <summary>Parses "#RRGGBB" or "#AARRGGBB"; falls back when the text is missing or malformed.</summary>
    public static MColor Parse(string? hex, MColor fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        var text = hex.TrimStart('#');
        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return fallback;
        }

        return text.Length switch
        {
            6 => new MColor((int)((value >> 16) & 0xFF), (int)((value >> 8) & 0xFF), (int)(value & 0xFF)),
            8 => new MColor((int)((value >> 16) & 0xFF), (int)((value >> 8) & 0xFF), (int)(value & 0xFF), (int)((value >> 24) & 0xFF)),
            _ => fallback
        };
    }
}
