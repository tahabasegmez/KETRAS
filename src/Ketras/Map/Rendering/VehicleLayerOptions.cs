namespace Ketras.Map.Rendering;

/// <summary>Symbol drawn at a vehicle's position.</summary>
internal enum VehicleMarkerKind
{
    /// <summary>A notched arrow pointing along the heading, with a dark outline.</summary>
    Arrow,

    /// <summary>An aircraft planform silhouette.</summary>
    Plane,

    /// <summary>A triangle.</summary>
    Triangle,

    /// <summary>A circle.</summary>
    Circle
}

/// <summary>How a vehicle's marker, trail and label are drawn.</summary>
internal sealed class VehicleLayerOptions
{
    /// <summary>The ownship: a white arrow with a Link-blue trail.</summary>
    public static VehicleLayerOptions Ownship { get; } = new();

    /// <summary>Traffic: a smaller arrow; each aircraft takes a colour from the palette.</summary>
    public static VehicleLayerOptions Traffic { get; } = new() { MarkerSize = 18 };

    public VehicleMarkerKind MarkerKind { get; init; } = VehicleMarkerKind.Arrow;

    /// <summary>Marker fill of the ownship; traffic takes a colour each from the map palette.</summary>
    public string MarkerColorHex { get; init; } = "#FFFFFF";

    /// <summary>On-screen length of the marker, in pixels: nose to tail for Arrow, the span for Plane.</summary>
    public double MarkerSize { get; init; } = 22;

    public bool ShowTrail { get; init; } = true;

    public string TrailColorHex { get; init; } = "#4DAAFC";

    public float TrailWidth { get; init; } = 2.2f;

    public bool ShowLabel { get; init; } = true;

    /// <summary>Text shown next to the marker instead of the aircraft's name; empty uses the name.</summary>
    public string LabelText { get; init; } = string.Empty;
}
