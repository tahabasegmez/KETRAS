namespace Ketras.Controls.Scene3D.Rendering;

/// <summary>A latitude / longitude pair as the renderer uses it.</summary>
internal readonly record struct GeoPointVO(double Latitude, double Longitude);

/// <summary>Flight phase of a path point, used only for its colour.</summary>
internal enum MissionPathPhase
{
    Takeoff,
    Cruise,
    Landing
}

/// <summary>One point of the planned route, in sequence order.</summary>
internal readonly record struct MissionPathPointVO(int Sequence, GeoPointVO Position, double AltitudeMeters, string Label, MissionPathPhase Phase);

/// <summary>A point of interest on the ground.</summary>
internal readonly record struct PointVO(GeoPointVO Position, string Label);

/// <summary>A volume drawn as a translucent extrusion between two altitudes (AMSL).</summary>
internal abstract class Fence
{
    public required string FenceId { get; init; }

    public string Label { get; init; } = string.Empty;

    /// <summary>True: stay inside (blue); false: keep out (red).</summary>
    public bool Inclusion { get; init; }

    public double MinAltitudeMeters { get; init; }

    public double MaxAltitudeMeters { get; init; } = 150;
}

/// <summary>A vertical cylinder.</summary>
internal sealed class CircularFence : Fence
{
    public GeoPointVO Center { get; init; }

    public double RadiusMeters { get; init; }
}

/// <summary>A vertical prism over a closed polygon.</summary>
internal sealed class PolygonFence : Fence
{
    public IReadOnlyList<GeoPointVO> Vertices { get; init; } = [];
}

/// <summary>Everything the viewport draws in one frame.</summary>
internal sealed class AirspaceScene
{
    public TrackedAircraft? Ownship { get; init; }

    public IReadOnlyList<TrackedAircraft> Traffic { get; init; } = [];

    public IReadOnlyList<Fence> Fences { get; init; } = [];

    public IReadOnlyList<MissionPathPointVO> MissionPath { get; init; } = [];

    public IReadOnlyList<PointVO> Points { get; init; } = [];
}
