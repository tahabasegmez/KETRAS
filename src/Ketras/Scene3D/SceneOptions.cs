using Ketras.Model.Geo;

namespace Ketras.Scene3D;

/// <summary>How the 3D scene is framed and what it draws.</summary>
public sealed record SceneOptions
{
    /// <summary>The scene's horizontal anchor (e.g. the planned home); null takes the first thing drawn.</summary>
    public GeoPoint? Origin { get; init; }

    /// <summary>
    /// The vertical datum: the scene's ground plane, metres AMSL (the home altitude). It is never taken from telemetry,
    /// or the ground, shadows and grid would rise and fall with the aircraft.
    /// </summary>
    public double HomeAltitudeMeters { get; init; }

    /// <summary>Baseline camera-to-target distance (m) when no aircraft is in view.</summary>
    public double CameraDistanceMeters { get; init; } = 1100;

    /// <summary>Camera yaw around up, degrees (0 = east, 90 = north).</summary>
    public double CameraYawDegrees { get; init; } = 35;

    /// <summary>Camera pitch, degrees; positive looks down.</summary>
    public double CameraPitchDegrees { get; init; } = 28;

    /// <summary>Pitch in overview when everything must fit the frame.</summary>
    public double OverviewCameraPitchDegrees { get; init; } = 72;

    /// <summary>Distance behind the followed aircraft (m).</summary>
    public double FollowDistanceMeters { get; init; } = 260;

    /// <summary>Height above the followed aircraft (m).</summary>
    public double FollowHeightMeters { get; init; } = 90;

    /// <summary>Draws the ground grid (without terrain).</summary>
    public bool ShowGrid { get; init; } = true;

    /// <summary>Scale of the scene.</summary>
    public double SceneScale { get; init; } = 1.0;

    /// <summary>Vertical field of view, degrees.</summary>
    public double FieldOfViewDegrees { get; init; } = 50;

    /// <summary>Distance (m) at which geometry starts fading into the haze.</summary>
    public double FogStartMeters { get; init; } = 700;

    /// <summary>Distance (m) at which geometry has fully faded.</summary>
    public double FogEndMeters { get; init; } = 9000;

    /// <summary>Each aircraft's silhouette on the ground below it.</summary>
    public bool ShowGroundShadows { get; init; } = true;

    /// <summary>A vertical line from each aircraft down to the ground.</summary>
    public bool ShowAltitudeDropLines { get; init; } = true;

    /// <summary>The ownship's trail extruded to the ground as a translucent curtain.</summary>
    public bool ShowTrailCurtain { get; init; } = true;

    /// <summary>Bearing ticks along the horizon.</summary>
    public bool ShowCompassRibbon { get; init; } = true;

    /// <summary>A reference runway strip at the origin (without terrain).</summary>
    public bool ShowRunway { get; init; } = true;

    /// <summary>How long (s) a trail point stays before it fades.</summary>
    public double TrailSeconds { get; init; } = 120;

    /// <summary>Smallest on-screen wingspan (px) of an aircraft symbol.</summary>
    public double MinimumAircraftPixels { get; init; } = 34;

    /// <summary>True-to-life wingspan (m) before the readability floor applies.</summary>
    public double AircraftWingspanMeters { get; init; } = 12;

    /// <summary>Overview framing radius (m).</summary>
    public double OverviewRadiusMeters { get; init; } = 900;

    /// <summary>The origin's latitude, for the renderer.</summary>
    internal double? HomeLatitude => Origin?.Latitude;

    /// <summary>The origin's longitude, for the renderer.</summary>
    internal double? HomeLongitude => Origin?.Longitude;
}
