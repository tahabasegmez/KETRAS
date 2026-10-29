using Ketras.Geo;
using Ketras.Vehicles;

namespace Ketras.Planning.Missions;

/// <summary>Factory: builds a <see cref="MissionItem"/> with its MAVLink parameters filled in per type (MAV_CMD spec, PX4 fixed-wing).</summary>
public static class MissionItemFactory
{
    /// <summary>Altitude when neither the options nor the settings give one.</summary>
    public const double DefaultAltitudeMeters = 120;

    /// <summary>Approach altitude of a new landing pattern.</summary>
    public const double DefaultLandingApproachAltitudeMeters = 40;

    /// <summary>Duration of a new timed loiter.</summary>
    public const double DefaultLoiterTimeSeconds = 30;

    /// <summary>Spacing of a new camera trigger distance.</summary>
    public const double DefaultCameraTriggerDistanceMeters = 50;

    /// <summary>A new item of <paramref name="type"/>; <paramref name="location"/> is ignored for items without one.</summary>
    public static MissionItem Create(
        MissionItemType type,
        GeoPoint? location = null,
        MissionItemOptions? options = null,
        MissionSettings? settings = null,
        VehicleLimits? limits = null)
    {
        limits ??= VehicleLimits.Default;
        var altitude = options?.AltitudeMeters
                       ?? (type == MissionItemType.LandingPattern ? DefaultLandingApproachAltitudeMeters : (double?)null)
                       ?? settings?.DefaultAltitudeMeters
                       ?? DefaultAltitudeMeters;
        var item = new MissionItem
        {
            Type = type,
            Location = MissionItemCatalog.UsesLocation(type) ? location : null,
            AltitudeMeters = altitude
        };

        var radius = options?.LoiterRadiusMeters ?? limits.DefaultLoiterRadiusMeters;
        var speed = options?.SpeedMps ?? settings?.CruiseSpeedMps ?? 0;

        return type switch
        {
            // Yaw is left unspecified; a fixed-wing flies the track, not a heading.
            MissionItemType.Takeoff or MissionItemType.Waypoint or MissionItemType.Land => item with { Param4 = double.NaN },
            MissionItemType.LoiterUnlimited => item with { Param3 = radius, Param4 = double.NaN },
            MissionItemType.LoiterTime => item with { Param1 = options?.LoiterTimeSeconds ?? DefaultLoiterTimeSeconds, Param3 = radius },
            // Param1 = 1: leave the loiter on the heading towards the next item.
            MissionItemType.LoiterToAltitude => item with { Param1 = 1, Param2 = radius },
            // Location is the touchdown; Param1 the touchdown altitude; the approach is the shortest within the slope limit.
            MissionItemType.LandingPattern => item with
            {
                Param2 = radius,
                Param3 = options?.DistanceMeters ?? ApproachLengthFor(altitude, limits.MaxLandingSlopeDegrees),
                Param4 = options?.HeadingDegrees ?? 0
            },
            // Param1 0 = airspeed; Param3 -1 = throttle unchanged.
            MissionItemType.ChangeSpeed => item with { Param1 = 0, Param2 = speed, Param3 = -1 },
            MissionItemType.TakePhoto => item with { Param3 = 1 },
            MissionItemType.CameraTriggerDistance => item with { Param1 = options?.CameraTriggerDistanceMeters ?? DefaultCameraTriggerDistanceMeters },
            MissionItemType.DelaySeconds => item with { Param1 = options?.DelaySeconds ?? 0 },
            MissionItemType.DoJump => item with { JumpTargetId = options?.JumpTargetId, Param1 = -1, Param2 = options?.JumpRepeatCount ?? 1 },
            _ => item
        };
    }

    /// <summary>Approach length for a descent of <paramref name="heightMeters"/> at the given slope, rounded up to 10 m, at least 50 m.</summary>
    public static double ApproachLengthFor(double heightMeters, double slopeDegrees) =>
        Math.Max(Math.Ceiling(heightMeters / Math.Tan(slopeDegrees * Math.PI / 180d) / 10) * 10, 50);
}
