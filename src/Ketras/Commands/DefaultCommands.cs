namespace Ketras.Commands;

/// <summary>An autopilot's native names for the modes the default commands switch to; null leaves a command out.</summary>
/// <param name="Manual">Manual flight.</param>
/// <param name="Hold">Hold / loiter at the current position.</param>
/// <param name="Mission">Fly the uploaded mission.</param>
/// <param name="ReturnToLaunch">Return to launch.</param>
/// <param name="Land">Land at the current position.</param>
/// <param name="Offboard">External control (e.g. a companion computer).</param>
public sealed record AutopilotModes(string? Manual, string? Hold, string? Mission, string? ReturnToLaunch, string? Land, string? Offboard)
{
    /// <summary>PX4 mode names (px4_custom_mode.h, as Ketras.Mavlink names them).</summary>
    public static AutopilotModes Px4 { get; } = new("MANUAL", "AUTO.LOITER", "AUTO.MISSION", "AUTO.RTL", "AUTO.LAND", "OFFBOARD");

    /// <summary>No named modes: no mode commands.</summary>
    public static AutopilotModes None { get; } = new(null, null, null, null, null, null);

    /// <summary>The modes of an autopilot profile ("px4", "generic"); "auto" is treated as PX4 until a vehicle is found.</summary>
    public static AutopilotModes For(string profile) => profile.ToLowerInvariant() switch
    {
        "px4" or "auto" => Px4,
        _ => None
    };
}

/// <summary>Names of the station actions the default commands use.</summary>
public static class StationActions
{
    /// <summary>Uploads the plan's mission.</summary>
    public const string UploadMission = "upload-mission";

    /// <summary>Uploads the plan's fences with the environment zones marked for the geofence.</summary>
    public const string UploadFences = "upload-fences";
}

/// <summary>
/// Factory: the commands every station offers for an autopilot: arm and disarm, takeoff, the mode switches, and the plan
/// uploads. Vehicle-side systems add theirs through plugins.
/// </summary>
public static class DefaultCommands
{
    /// <summary>Input key of the takeoff height above the vehicle.</summary>
    public const string TakeoffHeightInput = "takeoff-height";

    private const ushort ComponentArmDisarm = 400;
    private const ushort NavTakeoff = 22;

    /// <summary>The default commands for an autopilot profile name.</summary>
    public static IReadOnlyList<VehicleCommand> For(string profile)
    {
        var modes = AutopilotModes.For(profile);
        var list = new List<VehicleCommand>
        {
            new()
            {
                Id = "arm", Label = "Arm", Group = "Arming", Icon = "Power",
                Target = new MavlinkTarget(ComponentArmDisarm) { Params = [1, 0, 0, 0, 0, 0, 0] },
                Confirmation = "Arm the vehicle?",
                IsAvailable = vehicle => vehicle is { IsConnected: true, IsArmed: false }
            },
            new()
            {
                // No force flag: the autopilot refuses an in-air disarm, which is the safe default.
                Id = "disarm", Label = "Disarm", Group = "Arming", Icon = "PowerOff",
                Target = new MavlinkTarget(ComponentArmDisarm) { Params = [0, 0, 0, 0, 0, 0, 0] },
                Confirmation = "Disarm the vehicle?",
                IsAvailable = vehicle => vehicle is { IsConnected: true, IsArmed: true }
            },
            new()
            {
                Id = "takeoff", Label = "Takeoff", Group = "Flight", Icon = "PlaneTakeoff",
                Target = new MavlinkTarget(NavTakeoff)
                {
                    Params = [double.NaN, double.NaN, 0, double.NaN, double.NaN, double.NaN, 0],
                    RelativeAltitudeInput = TakeoffHeightInput
                },
                Inputs = [new CommandInput(TakeoffHeightInput, "Height above the vehicle (m)", "50")],
                Confirmation = "Take off?",
                IsAvailable = vehicle => vehicle is { IsConnected: true, IsLanded: not false }
            }
        };

        void Mode(string id, string label, string icon, string? mode, string? confirmation = null)
        {
            if (mode is not null)
            {
                list.Add(new VehicleCommand { Id = id, Label = label, Group = "Modes", Icon = icon, Target = new ModeTarget(mode), Confirmation = confirmation });
            }
        }

        Mode("mode-mission", "Mission", "Route", modes.Mission);
        Mode("mode-hold", "Hold", "CirclePause", modes.Hold);
        Mode("mode-rtl", "Return", "House", modes.ReturnToLaunch, "Return to launch?");
        Mode("mode-land", "Land", "PlaneLanding", modes.Land, "Land here?");
        Mode("mode-manual", "Manual", "Gamepad2", modes.Manual);
        Mode("mode-offboard", "Offboard", "Cpu", modes.Offboard);

        list.Add(new VehicleCommand
        {
            Id = "upload-mission", Label = "Upload mission", Group = "Plan", Icon = "Upload",
            Target = new StationActionTarget(StationActions.UploadMission),
            IsAvailable = vehicle => vehicle is { IsConnected: true }
        });
        list.Add(new VehicleCommand
        {
            Id = "upload-fences", Label = "Upload geofence", Group = "Plan", Icon = "Fence",
            Target = new StationActionTarget(StationActions.UploadFences),
            IsAvailable = vehicle => vehicle is { IsConnected: true }
        });
        return list;
    }
}
