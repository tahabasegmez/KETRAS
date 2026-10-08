using Ketras.Mavlink.Messages;
using Ketras.Station.Parameters;
using Ketras.Station.Setup;

namespace Ketras.Station.Tests;

public sealed class ActuatorTests
{
    private static readonly ActuatorMetadata Px4 = ActuatorMetadataParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "px4-sitl-actuators.json")));

    /// <summary>A fixed-wing with one motor and three surfaces on the simulator's outputs.</summary>
    private static Dictionary<string, double> FixedWing()
    {
        var values = new Dictionary<string, double>
        {
            ["CA_AIRFRAME"] = 1, ["CA_ROTOR_COUNT"] = 1, ["CA_SV_CS_COUNT"] = 3, ["SIM_GZ_EN"] = 1,
            ["CA_SV_CS0_TYPE"] = 1, ["CA_SV_CS1_TYPE"] = 2, ["CA_SV_CS2_TYPE"] = 3,
            ["SIM_GZ_EC_FUNC1"] = 101, ["SIM_GZ_SV_FUNC1"] = 201, ["SIM_GZ_SV_FUNC2"] = 202, ["SIM_GZ_SV_FUNC3"] = 203, ["SIM_GZ_EC_REV"] = 0
        };
        for (var i = 0; i < 3; i++)
        {
            values[$"CA_SV_CS{i}_TRQ_R"] = i == 0 ? -0.5 : i == 1 ? 0.5 : 0;
            values[$"CA_SV_CS{i}_TRQ_P"] = i == 2 ? 1 : 0;
            values[$"CA_SV_CS{i}_TRQ_Y"] = 0;
            values[$"CA_SV_CS{i}_TRIM"] = 0;
        }

        return values;
    }

    private static ParameterReader Reader(Dictionary<string, double> values) => name => values.TryGetValue(name, out var v) ? v : null;

    private static string? Surface(string param, long value) => value switch { 1 => "Left Aileron", 2 => "Right Aileron", 3 => "Elevator", _ => null };

    [Fact]
    public void The_px4_metadata_parses_every_geometry_and_output()
    {
        Assert.Contains(Px4.MixerConfigs, c => c.Title == "Fixed Wing");
        Assert.Contains(Px4.MixerConfigs, c => c.Title == "Tiltrotor VTOL");
        Assert.Equal("Motor 1", Px4.Functions[101].Label);
        Assert.Contains(Px4.Outputs, o => o.Label == "SIM_GZ");
        Assert.Throws<FormatException>(() => ActuatorMetadataParser.Parse("{}"));
        Assert.True(ActuatorCondition.Evaluate("CA_AIRFRAME==1", n => n == "CA_AIRFRAME" ? 1 : null));
        Assert.False(ActuatorCondition.Evaluate("CA_AIRFRAME==1", _ => null));
        Assert.True(ActuatorCondition.Evaluate("", _ => null));
    }

    [Fact]
    public void A_fixed_wing_resolves_its_geometry_outputs_and_test_actuators()
    {
        var model = ActuatorModel.Build(Px4, Reader(FixedWing()), Surface);

        Assert.True(model.HasMetadata);
        Assert.Equal("Fixed Wing", model.GeometryTitle);
        Assert.Equal(["motor", "servo"], model.Geometry.Select(g => g.ActuatorType));
        var surfaces = model.Geometry[1];
        Assert.Equal(3, surfaces.Items.Count);
        Assert.Equal("CA_SV_CS_COUNT", surfaces.Count!.Param);
        Assert.Contains(surfaces.Columns, c => c.IsType);
        var output = Assert.Single(model.Outputs);
        Assert.True(output.SubgroupsVisible);
        Assert.Equal(101, output.Subgroups[0].Channels[0].Function);
        Assert.Equal(["Motor 1", "Left Aileron", "Right Aileron", "Elevator"], model.TestActuators.Select(t => t.Label));
        Assert.True(model.TestActuators[0].IsMotor);
        Assert.All(model.TestActuators.Skip(1), t => Assert.True(t.IsControlSurface));
        Assert.False(model.HasUnsetRequiredFunctions);
        Assert.Equal("Elevator", model.GeometryLabels[203]);
    }

    [Fact]
    public void An_unassigned_surface_and_another_airframe_change_the_model()
    {
        var values = FixedWing();
        values["SIM_GZ_SV_FUNC3"] = 0;
        Assert.True(ActuatorModel.Build(Px4, Reader(values), Surface).HasUnsetRequiredFunctions);

        values["CA_AIRFRAME"] = 0;
        values["CA_ROTOR0_PX"] = 0.2;
        values["CA_ROTOR0_KM"] = 0.05;
        var multirotor = ActuatorModel.Build(Px4, Reader(values), Surface);
        Assert.Equal("Multirotor", multirotor.GeometryTitle);
        Assert.Contains(multirotor.Geometry[0].Items[0].Cells, c => c?.Param == "CA_ROTOR0_KM");
        Assert.Same(ActuatorModel.Empty, ActuatorModel.Build(null, Reader(values), Surface));
    }

    [Fact]
    public void Changing_a_surface_type_applies_the_rule_defaults()
    {
        var values = FixedWing();
        var model = ActuatorModel.Build(Px4, Reader(values), Surface);
        var surfaces = model.Geometry[1];

        // Elevator → left aileron: the rule sets the aileron's torques.
        var writes = ActuatorModel.RuleWrites(surfaces, surfaces.Items[2], 1, Reader(values));

        Assert.NotEmpty(writes);
        Assert.All(writes, w => Assert.StartsWith("CA_SV_CS2_", w.Param, StringComparison.Ordinal));
        Assert.Contains(writes, w => w.Param == "CA_SV_CS2_TRQ_P" && w.Value == 0);
    }

    [Fact]
    public async Task Testing_is_gated_capped_kept_alive_and_stopped_on_arming()
    {
        await using var station = await StationFixture.StartAsync(vehicleSetup: v =>
        {
            v.AcceptedCommands.Add(MavCmd.ActuatorTest);
        });
        var testing = station.Get<ActuatorTestWorkflow>();
        var motor = new TestActuator("Motor 1", 101, 0, 1, 0, true, false);
        var aileron = new TestActuator("Left Aileron", 201, -1, 1, 0, false, true);

        Assert.False(testing.Enable());
        var vehicle = await station.ConnectAsync();
        testing.Set(motor, 0.5);
        Assert.Null(testing.ValueOf(101));

        Assert.True(testing.Enable());
        testing.Set(motor, 0.9);
        Assert.Equal(ActuatorTestWorkflow.MotorCap, testing.ValueOf(101)!.Value, 6);
        await StationFixture.WaitForAsync(() => station.Vehicle.Commands.Count(c => c.Command == MavCmd.ActuatorTest && c.Params[4] == 1101) >= 3);
        var keepAlive = station.Vehicle.Commands.Last(c => c.Command == MavCmd.ActuatorTest);
        Assert.Equal((0.25f, 1f), (keepAlive.Params[0], keepAlive.Params[1]));

        testing.Stop(101);
        await StationFixture.WaitForAsync(() => station.Vehicle.Commands.Last(c => c.Command == MavCmd.ActuatorTest) is { } last && last.Params[1] == 0 && last.Params[0] == 0);

        testing.StartSequence([motor, aileron]);
        Assert.True(testing.IsSequenceRunning);
        Assert.Equal("Motor 1 +0.15", testing.SequenceText);

        var commands = station.Get<Commands.CommandService>();
        await commands.ExecuteAsync(new Commands.CommandRequest(commands.Commands.Single(c => c.Id == "arm")));
        await StationFixture.WaitForAsync(() => !testing.IsEnabled);
        Assert.Equal(EditBlock.Armed, ParameterRules.WhyNotEditable(vehicle));
        Assert.False(testing.IsSequenceRunning);
        Assert.NotEmpty(testing.StopReason);
    }
}
