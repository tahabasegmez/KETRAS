using Ketras.Mavlink.Messages;
using Ketras.Station.Commands;
using Ketras.Station.Parameters;
using Ketras.Station.Vehicles;

namespace Ketras.Station.Setup;

/// <summary>
/// The vehicle setup actions that are commands rather than parameter writes: reboot and reset to defaults. Each runs on
/// the active vehicle only while it can be edited (connected, disarmed, parameters read), and answers once.
/// </summary>
public sealed class VehicleSetupService(VehicleRegistry vehicles)
{
    /// <summary>Reboots the autopilot (MAV_CMD_PREFLIGHT_REBOOT_SHUTDOWN, param1 = 1).</summary>
    public Task<CommandResult> RebootAsync(CancellationToken cancellation = default) =>
        SendAsync(MavCmd.PreflightRebootShutdown, 1, cancellation);

    /// <summary>Resets every parameter to its firmware default (MAV_CMD_PREFLIGHT_STORAGE, param1 = 2, param2 = -1); it takes effect after a reboot.</summary>
    public Task<CommandResult> ResetParametersAsync(CancellationToken cancellation = default) =>
        SendAsync(MavCmd.PreflightStorage, 2, cancellation, param2: -1);

    /// <summary>Stores the current stick positions as the RC trims (MAV_CMD_PREFLIGHT_CALIBRATION, param4 = 2).</summary>
    public Task<CommandResult> CopyTrimsAsync(CancellationToken cancellation = default) =>
        SendAsync(MavCmd.PreflightCalibration, 0, cancellation, param4: 2);

    private async Task<CommandResult> SendAsync(MavCmd command, float param1, CancellationToken cancellation, float param2 = 0, float param4 = 0)
    {
        var vehicle = vehicles.Active;
        if (ParameterRules.WhyNotEditable(vehicle) is not EditBlock.None and var block)
        {
            return CommandResult.Refused(block switch
            {
                EditBlock.NoVehicle => "no vehicle is connected",
                EditBlock.LinkLost => "the link is lost",
                EditBlock.Armed => "the vehicle is armed",
                _ => "the parameters are still being read"
            });
        }

        var result = await vehicle!.Session.Commands.SendLongAsync(command, param1, param2, 0, param4, cancellation: cancellation);
        return result.IsAccepted ? CommandResult.Accepted()
            : new CommandResult(false, string.IsNullOrEmpty(result.Reason) ? result.Outcome.ToString() : result.Reason, result.Outcome == Mavlink.Protocols.CommandOutcome.NoResponse);
    }
}
