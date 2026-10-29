using Ketras.Mavlink.Messages;
using Ketras.Station.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace Ketras.Tests.Setup;

/// <summary>A clock that moves 600 ms each time it is read, so a stick held for a few samples settles.</summary>
internal sealed class SteppingTime : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero).UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Add(ref _ticks, TimeSpan.FromMilliseconds(600).Ticks), TimeSpan.Zero);
}

public sealed class RadioTests
{
    private static readonly Dictionary<string, (MavParamType Type, double Value)> RcParameters = new()
    {
        ["RC1_MIN"] = (MavParamType.Real32, 1000), ["RC1_MAX"] = (MavParamType.Real32, 2000), ["RC1_TRIM"] = (MavParamType.Real32, 1500), ["RC1_REV"] = (MavParamType.Real32, 1),
        ["RC2_MIN"] = (MavParamType.Real32, 1000), ["RC2_MAX"] = (MavParamType.Real32, 2000), ["RC2_TRIM"] = (MavParamType.Real32, 1500), ["RC2_REV"] = (MavParamType.Real32, 1),
        ["RC3_MIN"] = (MavParamType.Real32, 1000), ["RC3_MAX"] = (MavParamType.Real32, 2000), ["RC3_TRIM"] = (MavParamType.Real32, 1500), ["RC3_REV"] = (MavParamType.Real32, 1),
        ["RC4_MIN"] = (MavParamType.Real32, 1000), ["RC4_MAX"] = (MavParamType.Real32, 2000), ["RC4_TRIM"] = (MavParamType.Real32, 1500), ["RC4_REV"] = (MavParamType.Real32, 1),
        ["RC_MAP_ROLL"] = (MavParamType.Int32, 0), ["RC_MAP_PITCH"] = (MavParamType.Int32, 0), ["RC_MAP_YAW"] = (MavParamType.Int32, 0), ["RC_MAP_THROTTLE"] = (MavParamType.Int32, 0),
        ["RC_CHAN_CNT"] = (MavParamType.Int32, 0)
    };

    [Fact]
    public void Mode_slots_and_switches_map_and_the_battery_factor_scales()
    {
        Assert.Equal(1, RcRules.ActiveSlot(1000, 1000, 2000, 1500, 1));
        Assert.Equal(6, RcRules.ActiveSlot(2000, 1000, 2000, 1500, 1));
        Assert.Equal(1, RcRules.ActiveSlot(2000, 1000, 2000, 1500, -1));
        Assert.Equal(4, RcRules.ActiveSlot(1600, 1000, 2000, 1500, 1));
        Assert.True(RcRules.SwitchOn(1800, 0.75));
        Assert.False(RcRules.SwitchOn(1700, 0.75));
        Assert.True(RcRules.SwitchOn(1200, -0.25));
        Assert.Equal(11, RcRules.BatteryFactor(12.1, 10, 11)!.Value, 9);
        Assert.Null(RcRules.BatteryFactor(12, 10, 0));
    }

    [Fact]
    public async Task A_full_calibration_maps_the_sticks_and_writes_the_channels()
    {
        await using var station = await StationFixture.StartAsync(
            register: services => services.AddSingleton<TimeProvider>(new SteppingTime()),
            parameters: RcParameters);
        await station.ConnectAsync();
        var radio = station.Get<RadioCalibrationWorkflow>();
        ushort[] sticks = [1500, 1500, 1000, 1500, 1500];

        async Task Hold(int channel, ushort value, WizardStep next)
        {
            sticks[channel] = value;
            for (var i = 0; i < 6 && radio.Step != next; i++)
            {
                station.Vehicle.SendRc(sticks);
                await StationFixture.WaitForAsync(() => radio.Channels.Count > 0 && radio.Channels[channel] == value);
                await Task.Delay(30);
            }

            await StationFixture.WaitForAsync(() => radio.Step == next);
        }

        Assert.NotEmpty(radio.Start());
        station.Vehicle.SendRc(sticks);
        await StationFixture.WaitForAsync(() => radio.Channels.Count == 5);
        Assert.Empty(radio.Start());
        Assert.Equal(WizardStep.SticksNeutral, radio.Step);
        Assert.True(radio.CanNext);
        await radio.NextAsync();

        await Hold(2, 2000, WizardStep.ThrottleDown);
        await Hold(2, 1000, WizardStep.YawRight);
        await Hold(3, 2000, WizardStep.YawLeft);
        await Hold(3, 1500, WizardStep.YawLeft);
        await Hold(3, 1000, WizardStep.RollRight);
        await Hold(3, 1500, WizardStep.RollRight);
        await Hold(0, 1000, WizardStep.RollLeft);
        await Hold(0, 2000, WizardStep.PitchUp);
        await Hold(0, 1500, WizardStep.PitchUp);
        await Hold(1, 2000, WizardStep.PitchDown);
        await Hold(1, 1000, WizardStep.SwitchesMinMax);
        await radio.NextAsync();
        Assert.Equal(WizardStep.Complete, radio.Step);

        var written = await radio.NextAsync();

        Assert.Null(radio.Step);
        Assert.All(written, w => Assert.True(w.IsApplied, w.Name));
        Assert.Equal((1, 2, 4, 3), ((int)station.Vehicle.Parameter("RC_MAP_ROLL")!, (int)station.Vehicle.Parameter("RC_MAP_PITCH")!,
            (int)station.Vehicle.Parameter("RC_MAP_YAW")!, (int)station.Vehicle.Parameter("RC_MAP_THROTTLE")!));
        Assert.Equal(-1, station.Vehicle.Parameter("RC1_REV"));
        Assert.Equal(5, station.Vehicle.Parameter("RC_CHAN_CNT"));
    }
}
