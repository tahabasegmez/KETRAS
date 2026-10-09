using Ketras.Model.Geo;
using Ketras.Model.Missions;

namespace Ketras.Controls.Scene3D;

/// <summary>An aircraft in the scene: the ownship or traffic. Altitudes are metres AMSL, angles degrees.</summary>
/// <param name="Id">Stable identity; following an aircraft refers to it.</param>
/// <param name="Name">Label shown next to it.</param>
/// <param name="Position">Where it is.</param>
/// <param name="AltitudeMeters">Altitude AMSL.</param>
/// <param name="HeadingDegrees">Heading, 0…360.</param>
/// <param name="PitchDegrees">Pitch, positive nose up.</param>
/// <param name="RollDegrees">Roll, positive right wing down.</param>
/// <param name="GroundSpeedMps">Ground speed.</param>
/// <param name="VerticalSpeedMps">Vertical speed, positive up.</param>
/// <param name="Mode">Mode or category text shown in its label.</param>
public sealed record SceneAircraft(
    string Id,
    string Name,
    GeoPoint Position,
    double AltitudeMeters,
    double HeadingDegrees,
    double PitchDegrees = 0,
    double RollDegrees = 0,
    double GroundSpeedMps = 0,
    double VerticalSpeedMps = 0,
    string Mode = "");

/// <summary>A volume over an area between two altitudes AMSL: a fence or an environment zone.</summary>
/// <param name="Id">Stable identity.</param>
/// <param name="Label">Name shown with it; empty for none.</param>
/// <param name="Area">The area on the ground.</param>
/// <param name="StayIn">Stay inside (blue) rather than keep out (red).</param>
/// <param name="FloorMeters">Lower altitude AMSL.</param>
/// <param name="CeilingMeters">Upper altitude AMSL.</param>
public sealed record SceneVolume(string Id, string Label, Area Area, bool StayIn, double FloorMeters, double CeilingMeters);

/// <summary>A point of the planned route, in flying order.</summary>
/// <param name="Sequence">Its number shown in the scene.</param>
/// <param name="Position">Where it is.</param>
/// <param name="AltitudeMeters">Altitude AMSL.</param>
/// <param name="Label">Label shown with it.</param>
/// <param name="Phase">Flight phase, for its colour.</param>
public sealed record ScenePathPoint(int Sequence, GeoPoint Position, double AltitudeMeters, string Label, MissionPhase Phase);

/// <summary>A point of interest on the ground.</summary>
/// <param name="Label">Name shown with it.</param>
/// <param name="Position">Where it is.</param>
public sealed record ScenePoint(string Label, GeoPoint Position);

/// <summary>How the terrain surface is painted.</summary>
public enum SceneGround
{
    /// <summary>Grey relief with contour lines under a dark sky.</summary>
    Relief,

    /// <summary>Map imagery (<see cref="AirspaceView.SetImagery"/>) with thin contour lines under a blue sky.</summary>
    Imagery
}

/// <summary>One encoded map tile (PNG or JPEG).</summary>
/// <param name="X">Column.</param>
/// <param name="Y">Row, 0 at the north.</param>
/// <param name="Encoded">The image file's bytes.</param>
public sealed record SceneImageryTile(int X, int Y, byte[] Encoded);

/// <summary>Map tiles to drape over the terrain; tiles missing from the block leave the grey relief showing.</summary>
/// <param name="Range">The XYZ tile block.</param>
/// <param name="Tiles">The tiles of the block that are available.</param>
public sealed record SceneImagery(TileRange Range, IReadOnlyList<SceneImageryTile> Tiles);
