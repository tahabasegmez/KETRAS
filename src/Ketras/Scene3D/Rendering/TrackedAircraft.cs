namespace Ketras.Scene3D.Rendering;

/// <summary>One aircraft in the scene, the ownship or traffic.</summary>
internal sealed class TrackedAircraft
{
    public required string VehicleId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double AltitudeMeters { get; init; }
    public double HeadingDegrees { get; init; }
    public double PitchDegrees { get; init; }
    public double RollDegrees { get; init; }
    public double GroundSpeedMetersPerSecond { get; init; }
    public double VerticalSpeedMetersPerSecond { get; init; }
    public string Mode { get; init; } = "UNKNOWN";
    public string? ColorHex { get; init; }
}
