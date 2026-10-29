using Ketras.Mavlink.Messages;
using Ketras.Setup;

namespace Ketras.Tests.Setup;

public sealed class CalibrationTests
{
    [Fact]
    public void Calibration_texts_parse()
    {
        Assert.Null(CalibrationTextParser.Parse("Takeoff detected"));
        Assert.Equal(("accel", CalibrationEventKind.Started), (CalibrationTextParser.Parse("[cal] calibration started: 2 accel")!.Sensor, CalibrationEventKind.Started));
        Assert.Equal(40, CalibrationTextParser.Parse("[cal] progress &lt;40&gt;")!.Percent);
        Assert.Equal("down", CalibrationTextParser.Parse("[cal] down orientation detected")!.Side);
        Assert.Equal(CalibrationEventKind.SideDone, CalibrationTextParser.Parse("[cal] left side done, rotate to a different side")!.Kind);
        Assert.Equal(CalibrationEventKind.Done, CalibrationTextParser.Parse("[cal] calibration done: accel")!.Kind);
        var failed = CalibrationTextParser.Parse("[cal] calibration failed: motion detected")!;
        Assert.Equal((CalibrationEventKind.Failed, "motion detected"), (failed.Kind, failed.Text));
        Assert.Equal(["down", "up", "front"], CalibrationTextParser.CompassSides(2 + 16 + 32));
        Assert.Equal(6, CalibrationTextParser.CompassSides(null).Count);
    }

    [Fact]
    public async Task An_accelerometer_calibration_follows_the_sides_to_done_and_rereads_the_calibration()
    {
        await using var station = await StationFixture.StartAsync(vehicleSetup: v => v.AcceptedCommands.Add(MavCmd.PreflightCalibration));
        await station.ConnectAsync();
        var calibration = station.Get<CalibrationWorkflow>();

        Assert.True(await calibration.StartAsync(CalibrationKind.Accelerometer));
        var sent = station.Vehicle.Commands.Last(c => c.Command == MavCmd.PreflightCalibration);
        Assert.Equal(1, sent.Params[4]);
        Assert.Equal(CalibrationPhase.Starting, calibration.Phase);
        Assert.False(await calibration.StartAsync(CalibrationKind.Gyro));

        station.Vehicle.SendStatusText(MavSeverity.Info, "[cal] calibration started: 2 accel");
        await StationFixture.WaitForAsync(() => calibration.Phase == CalibrationPhase.Running);
        Assert.Equal(6, calibration.Sides.Count);
        station.Vehicle.SendStatusText(MavSeverity.Info, "[cal] down orientation detected");
        station.Vehicle.SendStatusText(MavSeverity.Info, "[cal] progress <17>");
        await StationFixture.WaitForAsync(() => calibration.Percent == 17);
        Assert.Equal(SideState.InProgress, calibration.Sides[0].State);
        station.Vehicle.SendStatusText(MavSeverity.Info, "[cal] down side done, rotate to a different side");
        await StationFixture.WaitForAsync(() => calibration.Sides[0].State == SideState.Completed);

        station.Vehicle.SendStatusText(MavSeverity.Info, "[cal] calibration done: accel");
        await StationFixture.WaitForAsync(() => calibration.Phase == CalibrationPhase.Done);
        Assert.All(calibration.Sides, s => Assert.Equal(SideState.Completed, s.State));
        Assert.Contains("calibration done: accel", calibration.Lines);
        calibration.Close();
        Assert.Equal(CalibrationPhase.Idle, calibration.Phase);
    }

    [Fact]
    public async Task A_refused_start_fails_and_cancel_sends_the_empty_command()
    {
        await using var station = await StationFixture.StartAsync();
        await station.ConnectAsync();
        var calibration = station.Get<CalibrationWorkflow>();

        Assert.False(await calibration.StartAsync(CalibrationKind.Gyro));
        Assert.Equal(CalibrationPhase.Failed, calibration.Phase);
        Assert.NotEmpty(calibration.Outcome);

        station.Vehicle.AcceptedCommands.Add(MavCmd.PreflightCalibration);
        Assert.True(await calibration.StartAsync(CalibrationKind.Compass));
        Assert.Equal(1, station.Vehicle.Commands.Last().Params[1]);
        await calibration.CancelAsync();
        Assert.Equal(CalibrationPhase.Cancelling, calibration.Phase);
        Assert.All(station.Vehicle.Commands.Last().Params, p => Assert.Equal(0, p));
        station.Vehicle.SendStatusText(MavSeverity.Info, "[cal] calibration cancelled");
        await StationFixture.WaitForAsync(() => calibration.Phase == CalibrationPhase.Cancelled);
    }
}
