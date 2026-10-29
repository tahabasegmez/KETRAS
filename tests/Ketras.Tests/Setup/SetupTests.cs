using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ketras.Comms;
using Ketras.Controls;
using Ketras.Mavlink.Messages;
using Ketras.Parameters;
using Ketras.Planning;
using Ketras.Setup;
using Ketras.Utilities;
using Ketras.Vehicles;
using Microsoft.Extensions.DependencyInjection;

namespace Ketras.Tests.Setup;

[Collection(AvaloniaCollection.Name)]
public sealed class SetupTests
{
    private const string Metadata = """
        {"version":1,"parameters":[
          {"name":"FW_AIRSPD_MIN","type":"Float","group":"FW TECS","shortDesc":"Minimum Airspeed","units":"m/s","min":0.5,"max":40,"decimalPlaces":1,"default":10},
          {"name":"FW_AIRSPD_MAX","type":"Float","group":"FW TECS","shortDesc":"Maximum Airspeed","units":"m/s","min":0.5,"max":40,"default":25},
          {"name":"GF_ACTION","type":"Int32","group":"Geofence","shortDesc":"Geofence violation action","default":2,
           "values":[{"value":0,"description":"None"},{"value":1,"description":"Warning"},{"value":2,"description":"Hold mode"}]},
          {"name":"BAT1_SOURCE","type":"Int32","group":"Battery Calibration","shortDesc":"Battery monitoring source","default":0,
           "values":[{"value":-1,"description":"Disabled"},{"value":0,"description":"Power Module"}]},
          {"name":"BAT1_N_CELLS","type":"Int32","group":"Battery Calibration","shortDesc":"Number of cells","default":0},
          {"name":"SIM_GZ_SV_FUNC1","type":"Int32","group":"Actuator Outputs","shortDesc":"SIM_GZ Servo 1 Output Function","default":0,
           "values":[{"value":0,"description":"Disabled"},{"value":201,"description":"Servo 1"},{"value":202,"description":"Servo 2"}]},
          {"name":"CA_SV_CS0_TYPE","type":"Int32","group":"Geometry","shortDesc":"Control Surface 0 type","default":0,"values":[{"value":0,"description":"(Not set)"},{"value":1,"description":"Left Aileron"},{"value":2,"description":"Right Aileron"},{"value":3,"description":"Elevator"}]},
          {"name":"CA_SV_CS1_TYPE","type":"Int32","group":"Geometry","shortDesc":"Control Surface 1 type","default":0,"values":[{"value":0,"description":"(Not set)"},{"value":1,"description":"Left Aileron"},{"value":2,"description":"Right Aileron"},{"value":3,"description":"Elevator"}]},
          {"name":"CA_SV_CS2_TYPE","type":"Int32","group":"Geometry","shortDesc":"Control Surface 2 type","default":0,"values":[{"value":0,"description":"(Not set)"},{"value":1,"description":"Left Aileron"},{"value":2,"description":"Right Aileron"},{"value":3,"description":"Elevator"}]}
        ]}
        """;

    private static readonly Dictionary<string, (MavParamType, double)> Parameters = new()
    {
        ["SYS_AUTOSTART"] = (MavParamType.Int32, 2100),
        ["CA_AIRFRAME"] = (MavParamType.Int32, 1),
        ["GF_ACTION"] = (MavParamType.Int32, 2),
        ["GF_MAX_HOR_DIST"] = (MavParamType.Real32, 0),
        ["BAT1_SOURCE"] = (MavParamType.Int32, 0),
        ["BAT1_N_CELLS"] = (MavParamType.Int32, 0),
        ["BAT1_V_EMPTY"] = (MavParamType.Real32, 3.5),
        ["BAT1_V_CHARGED"] = (MavParamType.Real32, 4.05),
        ["CAL_GYRO0_ID"] = (MavParamType.Int32, 1),
        ["CAL_ACC0_ID"] = (MavParamType.Int32, 0),
        ["CAL_MAG0_ID"] = (MavParamType.Int32, 1),
        ["SYS_HAS_MAG"] = (MavParamType.Int32, 1),
        ["SENS_BOARD_ROT"] = (MavParamType.Int32, 0),
        ["COM_RC_IN_MODE"] = (MavParamType.Int32, 1),
        ["RC_MAP_ROLL"] = (MavParamType.Int32, 1),
        ["RC_MAP_THROTTLE"] = (MavParamType.Int32, 3),
        ["RC_MAP_FLTMODE"] = (MavParamType.Int32, 5),
        ["RC5_MIN"] = (MavParamType.Real32, 1000),
        ["RC5_MAX"] = (MavParamType.Real32, 2000),
        ["RC5_TRIM"] = (MavParamType.Real32, 1500),
        ["RC5_REV"] = (MavParamType.Real32, 1),
        ["COM_FLTMODE6"] = (MavParamType.Int32, 5),
        ["RC_MAP_RETURN_SW"] = (MavParamType.Int32, 6),
        ["RC_RETURN_TH"] = (MavParamType.Real32, 0.75),
        ["BAT1_V_DIV"] = (MavParamType.Real32, 10),
        ["CA_ROTOR_COUNT"] = (MavParamType.Int32, 1),
        ["CA_SV_CS_COUNT"] = (MavParamType.Int32, 3),
        ["CA_SV_CS0_TYPE"] = (MavParamType.Int32, 1),
        ["CA_SV_CS1_TYPE"] = (MavParamType.Int32, 2),
        ["CA_SV_CS2_TYPE"] = (MavParamType.Int32, 3),
        ["CA_SV_CS2_TRQ_P"] = (MavParamType.Real32, 1),
        ["CA_SV_CS2_TRQ_R"] = (MavParamType.Real32, 0),
        ["SIM_GZ_EN"] = (MavParamType.Int32, 1),
        ["SIM_GZ_EC_FUNC1"] = (MavParamType.Int32, 101),
        ["SIM_GZ_SV_FUNC1"] = (MavParamType.Int32, 201),
        ["SIM_GZ_SV_FUNC2"] = (MavParamType.Int32, 202),
        ["SIM_GZ_SV_FUNC3"] = (MavParamType.Int32, 203),
        ["SIM_GZ_EC_REV"] = (MavParamType.Int32, 0)
    };

    private static AppFixture Create(string? filePath = null) => AppFixture.Create(
        register: services => services.AddSingleton<IFileDialogService>(new FixedFileDialogs(filePath)),
        parameters: Parameters,
        vehicleSetup: v =>
        {
            v.AcceptedCommands.Add(MavCmd.PreflightCalibration);
            v.AcceptedCommands.Add(MavCmd.ActuatorTest);
            v.ComponentMetadata = ("mftp://etc/general.json", 7);
            v.Files["/etc/general.json"] = Encoding.UTF8.GetBytes(
                """{"version":1,"metadataTypes":[{"type":1,"uri":"mftp://etc/parameters.json","fileCrc":0},{"type":5,"uri":"mftp://etc/actuators.json","fileCrc":0}]}""");
            v.Files["/etc/actuators.json"] = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "px4-sitl-actuators.json"));
            v.Files["/etc/parameters.json"] = Encoding.UTF8.GetBytes(Metadata);
        });

    private static async Task ConnectAsync(AppFixture app)
    {
        await app.StartAsync();
        await app.Get<ConnectionService>().ConnectAsync(new LinkProfile { Name = "Bench" });
        await AppFixture.WaitForAsync(() => app.Get<VehicleRegistry>().Active is { Parameters.IsLoaded: true });
        await AppFixture.WaitForAsync(() => app.Get<SetupContext>() is { CanEdit: true, IsMetadataLoading: false });
    }

    [Fact]
    public async Task Without_a_vehicle_the_banner_says_so_and_nothing_can_be_staged()
    {
        await using var app = Create();
        var setup = app.Get<SetupViewModel>();

        Assert.Equal(EditBlock.NoVehicle, app.Get<SetupContext>().Block);
        Assert.Equal("No vehicle connected: values appear once a vehicle is connected.", setup.BannerModel.StateText);
        Assert.False(app.Get<SetupContext>().Set("FW_AIRSPD_MIN", 13));
        Assert.Equal(["Summary", "Sensors", "Radio", "Power", "Actuators", "Parameters"], setup.Pages.Select(p => p.Title));
    }

    [Fact]
    public async Task The_summary_shows_the_identity_and_the_checklist_marks_the_power_page()
    {
        await using var app = Create();
        await ConnectAsync(app);
        var setup = app.Get<SetupViewModel>();

        var identity = setup.Summary.Identity.ToDictionary(r => r.Key, r => r.Value);
        Assert.Equal("2100", identity["Airframe"]);
        Assert.Equal("from the vehicle", identity["Descriptions"]);
        Assert.False(setup.Summary.HasWarnings);
        Assert.Equal(["Sensors", "Radio", "Power", "Actuators"], setup.Summary.Checklist.Select(r => r.Title));
        Assert.Equal(SetupState.Ready, setup.Summary.Checklist[3].State);
        var sensors = setup.Summary.Checklist[0];
        var power = setup.Summary.Checklist[2];
        Assert.Equal(SetupState.Required, sensors.State);
        Assert.Equal(SetupState.Ready, setup.Summary.Checklist[1].State);
        Assert.Equal(SetupState.Required, power.State);
        Assert.True(setup.Pages[3].NeedsAttention);

        power.OpenCommand.Execute(null);
        Assert.Same(setup.Pages[3], setup.SelectedPage);
    }

    [Fact]
    public async Task An_edit_is_pending_until_apply_writes_it_and_the_checklist_follows()
    {
        await using var app = Create();
        await ConnectAsync(app);
        var setup = app.Get<SetupViewModel>();
        var cells = setup.Power.Sections[0].Shown.Single(f => f.Name == "BAT1_N_CELLS");
        Assert.Equal("Number of cells", cells.Label);

        cells.Text = "4";
        cells.CommitCommand.Execute(null);

        Assert.True(cells.IsPending);
        Assert.True(setup.BannerModel.HasPending);
        Assert.Equal("1 change(s) not written", setup.BannerModel.PendingText);
        Assert.Equal(0, app.Vehicle.Parameter("BAT1_N_CELLS"));

        await setup.BannerModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(4, app.Vehicle.Parameter("BAT1_N_CELLS"));
        await AppFixture.WaitForAsync(() => !cells.IsPending && cells.Text == "4");
        Assert.False(setup.BannerModel.HasPending);
        Assert.Equal(SetupState.Ready, setup.Summary.Checklist[2].State);
        Assert.Contains(setup.Log.Rows, r => r.Text == "BAT1_N_CELLS = 4");
    }

    [Fact]
    public async Task An_out_of_range_value_is_refused_and_a_choice_stages_its_value()
    {
        await using var app = Create();
        await ConnectAsync(app);
        var page = app.Get<SetupViewModel>().Parameters;
        page.Search = "airspd_min";
        var min = Assert.IsType<ParameterRowViewModel>(Assert.Single(page.Items)).Field;

        min.Text = "99";
        min.CommitCommand.Execute(null);
        Assert.Contains("40 m/s", min.Problem, StringComparison.Ordinal);
        Assert.False(min.IsPending);

        page.Search = string.Empty;
        page.SelectedGroup = page.Groups.Single(g => g.Key == "group:Geofence");
        var action = Assert.IsType<ParameterRowViewModel>(Assert.Single(page.Items)).Field;
        Assert.Equal("Hold mode", action.SelectedChoice!.Label);
        action.SelectedChoice = action.Choices.Single(c => c.Label == "Warning");
        Assert.Equal(1, app.Get<PendingParameterStore>()["GF_ACTION"]);

        app.Get<SetupContext>().Discard();
        await AppFixture.WaitForAsync(() => action.SelectedChoice?.Label == "Hold mode");
    }

    [Fact]
    public async Task The_list_filters_by_text_modified_curated_group_and_favourites()
    {
        await using var app = Create();
        await ConnectAsync(app);
        var page = app.Get<SetupViewModel>().Parameters;
        Assert.Equal($"{page.Items.Count} of {app.Get<SetupContext>().Values.Count}", page.CountText);

        page.ModifiedOnly = true;
        Assert.Equal(["CA_SV_CS0_TYPE", "CA_SV_CS1_TYPE", "CA_SV_CS2_TYPE", "FW_AIRSPD_MIN", "SIM_GZ_SV_FUNC1"], page.Items.OfType<ParameterRowViewModel>().Select(r => r.Field.Name));
        page.ModifiedOnly = false;

        page.SelectedGroup = page.Groups.Single(g => g.Key == "curated:failsafes");
        Assert.Contains(page.Items, i => i is ParameterSectionItem { Title: "GEOFENCE" });
        Assert.Equal(["GF_ACTION", "GF_MAX_HOR_DIST"], page.Items.OfType<ParameterRowViewModel>().Select(r => r.Field.Name));

        page.Items.OfType<ParameterRowViewModel>().First().ToggleFavouriteCommand.Execute(null);
        page.SelectedGroup = page.Groups.Single(g => g.Key == "favourites");
        Assert.Equal(["GF_ACTION"], page.Items.OfType<ParameterRowViewModel>().Select(r => r.Field.Name));
        Assert.Equal("GF_ACTION", app.Get<Ketras.Settings.SettingsStore>().Get(ParametersPageViewModel.FavouritesSetting));
    }

    [Fact]
    public async Task A_saved_file_loaded_back_with_a_change_stages_only_the_selected_rows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ketras-{Guid.NewGuid():N}.params");
        try
        {
            await using var app = Create(path);
            await ConnectAsync(app);
            var page = app.Get<SetupViewModel>().Parameters;

            await page.SaveFileCommand.ExecuteAsync(null);
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("\tSYS_AUTOSTART\t2100\t6", text, StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, text.Replace("\tSYS_AUTOSTART\t2100", "\tSYS_AUTOSTART\t2105", StringComparison.Ordinal)
                .Replace("\tGF_ACTION\t2", "\tGF_ACTION\t1", StringComparison.Ordinal) + "1\t1\tNOT_HERE\t1\t6\n");

            await page.LoadFileCommand.ExecuteAsync(null);
            Assert.True(page.IsReviewing);
            Assert.Equal(["GF_ACTION", "SYS_AUTOSTART", "NOT_HERE"], page.Review.Select(r => r.Name));
            Assert.False(page.Review[2].CanSelect);
            page.Review[0].IsSelected = false;
            page.StageReviewCommand.Execute(null);

            Assert.False(page.IsReviewing);
            Assert.Equal(["SYS_AUTOSTART"], app.Get<PendingParameterStore>().Pending.Keys);
            Assert.Equal(2105, app.Get<PendingParameterStore>()["SYS_AUTOSTART"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Reboot_asks_first_then_sends_the_command_and_logs_the_answer()
    {
        await using var app = Create();
        await ConnectAsync(app);
        var page = app.Get<SetupViewModel>().Parameters;

        page.RebootCommand.Execute(null);
        Assert.True(page.IsConfirming);
        Assert.DoesNotContain(app.Vehicle.Commands, c => c.Command == MavCmd.PreflightRebootShutdown);
        await page.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains(app.Vehicle.Commands, c => c.Command == MavCmd.PreflightRebootShutdown && c.Params[0] == 1);
        // The fake vehicle does not implement reboot; its refusal is what the log reports.
        Assert.Contains(app.Get<SetupViewModel>().Log.Rows, r => r.Text == "Reboot: refused (unsupported).");
    }

    [Fact]
    public async Task The_planner_shows_the_breach_settings_once_a_vehicle_is_connected()
    {
        await using var app = Create();
        var fences = app.Get<PlanningViewModel>().Fences;
        Assert.False(fences.Breach.IsVisible);

        await ConnectAsync(app);

        Assert.True(fences.Breach.IsVisible);
        Assert.Equal(["GF_ACTION", "GF_MAX_HOR_DIST"], fences.Breach.Shown.Select(f => f.Name));
        fences.Breach.Shown[1].Text = "500";
        fences.Breach.Shown[1].CommitCommand.Execute(null);
        Assert.True(fences.Pending.HasPending);
        Assert.Equal(500, app.Get<PendingParameterStore>()["GF_MAX_HOR_DIST"]);
    }

    [Fact]
    public async Task An_accelerometer_calibration_asks_first_runs_and_marks_the_sensor_calibrated()
    {
        await using var app = Create();
        await ConnectAsync(app);
        var sensors = app.Get<SetupViewModel>().Sensors;
        var accel = sensors.Sensors.Single(s => s.Kind == CalibrationKind.Accelerometer);
        Assert.True(accel.IsRequired);
        Assert.False(sensors.Sensors.Single(s => s.Kind == CalibrationKind.Airspeed).IsVisible);

        accel.CalibrateCommand.Execute(null);
        Assert.True(sensors.IsAsking);
        Assert.True(sensors.ShowsOrientation);
        Assert.False(sensors.Sensors[1].CanCalibrate);
        await sensors.StartCommand.ExecuteAsync(null);
        Assert.Contains(app.Vehicle.Commands, c => c.Command == MavCmd.PreflightCalibration && c.Params[4] == 1);

        app.Vehicle.SendStatusText(MavSeverity.Info, "[cal] calibration started: 2 accel");
        await AppFixture.WaitForAsync(() => sensors.Sides.Count == 6);
        Assert.Equal("Level", sensors.Sides[0].Label);
        app.Vehicle.SendStatusText(MavSeverity.Info, "[cal] calibration done: accel");
        await AppFixture.WaitForAsync(() => sensors.IsSucceeded);
        Assert.True(sensors.CanClose);
        Assert.Contains(app.Get<SetupViewModel>().Log.Rows, r => r.Text == "Accelerometer calibrated.");

        sensors.CloseSessionCommand.Execute(null);
        Assert.False(sensors.IsSessionVisible);
    }

    [Fact]
    public async Task The_radio_page_shows_the_channels_the_selected_slot_and_the_switches()
    {
        await using var app = Create();
        await ConnectAsync(app);
        var radio = app.Get<SetupViewModel>().Radio;
        Assert.True(radio.NoInput);
        Assert.Contains("RC calibration is not required", radio.InputNote, StringComparison.Ordinal);

        app.Vehicle.SendRc(1500, 1500, 1000, 1500, 2000, 1900);
        await AppFixture.WaitForAsync(() => radio.Channels.Count == 6);

        Assert.Equal("Roll (aileron)", radio.Channels[0].Function);
        Assert.Equal("Throttle", radio.Channels[2].Function);
        Assert.True(radio.Slots[5].IsActive);
        var returnSwitch = Assert.Single(radio.SwitchStates);
        Assert.True(returnSwitch.IsActive);
        Assert.Equal("ch6 ON", returnSwitch.Detail);

        radio.CalibrateCommand.Execute(null);
        Assert.True(radio.IsCalibrating);
        Assert.True(radio.CanNext);
        radio.CancelCalibrationCommand.Execute(null);
        Assert.False(radio.IsCalibrating);
    }

    [Fact]
    public async Task The_voltage_divider_calculator_stages_the_scaled_factor()
    {
        await using var app = Create();
        await ConnectAsync(app);
        var calculator = app.Get<SetupViewModel>().Power.Calculators.Single(c => c.Parameter == "BAT1_V_DIV");
        Assert.True(calculator.IsVisible);
        Assert.False(calculator.CanCalculate);

        app.Vehicle.SendBattery(11, 1, 80);
        await AppFixture.WaitForAsync(() => calculator.CanCalculate);
        calculator.Measured = 12.1.ToString(System.Globalization.CultureInfo.CurrentCulture);
        calculator.CalculateCommand.Execute(null);

        Assert.Equal(11, app.Get<PendingParameterStore>()["BAT1_V_DIV"]!.Value, 4);
    }

    [Fact]
    public async Task The_actuators_page_shows_the_geometry_outputs_and_gates_the_test()
    {
        await using var app = Create();
        await ConnectAsync(app);
        var setup = app.Get<SetupViewModel>();
        var actuators = setup.Actuators;

        Assert.True(actuators.HasMetadata);
        Assert.Equal("GEOMETRY: FIXED WING", actuators.GeometryTitle);
        Assert.Equal(["Motors", "Control Surfaces"], actuators.Geometry.Select(g => g.Label));
        var output = Assert.Single(actuators.Outputs);
        var functionField = output.Subgroups[1].Rows[0].Cells[0].Field!;
        Assert.Equal("Servo 1 (Left Aileron)", functionField.SelectedChoice!.Label);
        Assert.Equal(["Motor 1", "Left Aileron", "Right Aileron", "Elevator"], actuators.Tests.Select(t => t.Label));

        // Elevator becomes a left aileron: the rule's torques are staged with the type.
        var elevatorType = actuators.Geometry[1].Rows[2].Cells.First(c => c.Field?.Name == "CA_SV_CS2_TYPE").Field!;
        elevatorType.SelectedChoice = elevatorType.Choices.First(c => c.Value == 1);
        Assert.True(app.Get<PendingParameterStore>().Count > 1);
        Assert.Equal(0, app.Get<PendingParameterStore>()["CA_SV_CS2_TRQ_P"]);
        app.Get<SetupContext>().Discard();

        setup.SelectedPage = setup.Pages[4];
        actuators.Tests[0].Value = 0.2;
        Assert.False(actuators.Tests[0].IsActive);
        Assert.Equal(0, actuators.Tests[0].Value);
        actuators.PropellersRemoved = true;
        actuators.Tests[0].Value = 0.2;
        await AppFixture.WaitForAsync(() => app.Vehicle.Commands.Any(c => c.Command == MavCmd.ActuatorTest && c.Params[4] == 1101));
        await AppFixture.WaitForAsync(() => actuators.Tests[0].IsActive);

        setup.SelectedPage = setup.Pages[0];
        await AppFixture.WaitForAsync(() => !actuators.PropellersRemoved && !actuators.Tests[0].IsActive);
        await AppFixture.WaitForAsync(() => app.Vehicle.Commands.Last(c => c.Command == MavCmd.ActuatorTest).Params[1] == 0);
    }

    [AvaloniaFact]
    public async Task The_setup_screen_shows_the_banner_the_pages_and_the_parameter_rows()
    {
        await using var app = Create();
        await ConnectAsync(app);
        var setup = app.Get<SetupViewModel>();
        var window = new Window { Width = 1400, Height = 900, Content = new SplitScreenView { DataContext = setup } };
        window.Show();
        Pump(window);

        Assert.Equal(6, Find<ListBox>(window, "PageList").ItemCount);
        Assert.Contains(window.GetVisualDescendants().OfType<SummaryPageView>(), v => v.IsEffectivelyVisible);
        Assert.False(Find<Border>(window, "ApplyBar").IsVisible);

        setup.SelectedPage = setup.Pages[5];
        Pump(window);
        Assert.NotEmpty(window.GetVisualDescendants().OfType<ParameterFieldView>());

        setup.SelectedPage = setup.Pages[4];
        Pump(window);
        Assert.True(Find<Border>(window, "Testing").IsEffectivelyVisible);
        Assert.Equal(4, setup.Actuators.Tests.Count);

        app.Get<SetupContext>().Set("FW_AIRSPD_MIN", 13);
        Pump(window);
        Assert.True(Find<Border>(window, "ApplyBar").IsVisible);
        Assert.Equal("1 change(s) not written", Find<TextBlock>(window, "PendingText").Text);
        window.Close();
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(control => control.Name == name);
}
