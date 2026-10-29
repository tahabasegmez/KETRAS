using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ketras.Controls.Instruments;
using Ketras.Environment.Weather;
using Ketras.Model.Vehicles;
using Ketras.Shell;
using Ketras.Station.Links;
using Ketras.Station.Vehicles;
using Ketras.Telemetry;

namespace Ketras.Tests.Telemetry;

[Collection(AvaloniaCollection.Name)]
public sealed class TelemetryAndWeatherTests
{
    private static VehicleSnapshot Snapshot(double airspeed, bool armed = true) => new()
    {
        Id = new VehicleId(1),
        HeadingDegrees = 274,
        AirspeedMps = airspeed,
        GroundSpeedMps = 21.04,
        AltitudeAmslMeters = 612.4,
        RelativeAltitudeMeters = 120,
        ClimbRateMps = 1.5,
        RollDegrees = 10,
        PitchDegrees = 2,
        BatteryPercent = 76,
        IsArmed = armed
    };

    [Fact]
    public async Task The_panel_reads_every_value_and_colours_the_height_above_terrain()
    {
        await using var app = AppFixture.Create();
        await app.StartAsync();
        var panel = app.Get<TelemetryPanelViewModel>();
        Assert.Equal(("--", "--", "--"), (panel.Amsl, panel.Agl, panel.Airspeed));

        await app.Get<ConnectionService>().ConnectAsync(new LinkProfile { Name = "Bench" });
        await AppFixture.WaitForAsync(() => app.Get<VehicleRegistry>().Active is { Parameters.IsLoaded: true });
        app.Vehicle.SendPosition(47.39, 8.54, 520, 30, headingDegrees: 90, northMps: 15, eastMps: 0);
        await AppFixture.WaitForAsync(() => panel.Relative == "30 m");

        var units = app.Get<Ketras.Utilities.UnitService>();
        panel.Apply(Snapshot(18));
        Assert.Equal((units.Altitude(612.4), units.Altitude(120), units.Speed(18), units.Speed(21.04), "+" + units.Speed(1.5), "76 %"),
            (panel.Amsl, panel.Relative, panel.Airspeed, panel.GroundSpeed, panel.Climb, panel.Battery));
        Assert.Equal((10.0, 2.0, 274.0), (panel.Roll!.Value, panel.Pitch!.Value, panel.Heading!.Value));
        Assert.False(panel.IsAirspeedWarning);

        // Above the vehicle's maximum airspeed while flying; on the ground any airspeed is fine.
        panel.Apply(Snapshot(40));
        Assert.True(panel.IsAirspeedWarning);
        panel.Apply(Snapshot(40, armed: false));
        Assert.False(panel.IsAirspeedWarning);

        panel.ApplyClearance(new TerrainClearance(95, null, null, false));
        Assert.Equal((units.Altitude(95), ReadoutState.Normal), (panel.Agl, panel.AglState));
        panel.ApplyClearance(new TerrainClearance(40, 12, "terrain in 12 s", true));
        Assert.Equal((ReadoutState.Warning, true), (panel.AglState, panel.IsAglWarning));
        Assert.EndsWith(": terrain in 12 s", panel.AglDetail, StringComparison.Ordinal);
        panel.ApplyClearance(new TerrainClearance(-3, null, null, true));
        Assert.True(panel.IsAglError);
    }

    [Fact]
    public async Task The_weather_shows_the_latest_sample_with_where_the_wind_comes_from()
    {
        await using var app = AppFixture.Create();
        await app.StartAsync();
        var weather = app.Get<WeatherViewModel>();
        Assert.False(weather.HasData);
        Assert.Equal("no data", weather.Status);

        await app.Get<ConnectionService>().ConnectAsync(new LinkProfile { Name = "Bench" });
        await AppFixture.WaitForAsync(() => app.Get<VehicleRegistry>().Active is { Parameters.IsLoaded: true });
        app.Vehicle.SendWind(northMps: 4, eastMps: 4, downMps: 0);
        app.Vehicle.SendAir(18.5, 55);
        await AppFixture.WaitForAsync(() => weather.HasData && weather.Humidity != "--" && weather.WindSpeed is not null);

        Assert.Equal(225, weather.WindFrom!.Value, 6);
        Assert.EndsWith("· 225° SW", weather.Wind, StringComparison.Ordinal);
        Assert.Equal(app.Get<Ketras.Utilities.UnitService>().Temperature(18.5), weather.Temperature);
        Assert.StartsWith("Vehicle · ", weather.Status, StringComparison.Ordinal);
        Assert.Equal(("N", "NE", "SW", "N"), (WeatherViewModel.CompassPoint(10), WeatherViewModel.CompassPoint(44), WeatherViewModel.CompassPoint(225), WeatherViewModel.CompassPoint(359)));

        weather.ToggleExpandedCommand.Execute(null);
        Assert.False(weather.IsExpanded);
    }

    [AvaloniaFact]
    public async Task Planning_and_3d_show_the_telemetry_panel_and_the_credits_on_hover()
    {
        await using var app = AppFixture.Create();
        var window = new MainWindow { DataContext = app.Get<ShellViewModel>() };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var panel = window.GetVisualDescendants().OfType<TelemetryPanelView>().Single();
        Assert.Equal(96, panel.GetVisualDescendants().OfType<AttitudeIndicator>().Single().Bounds.Height);
        Assert.True(panel.Bounds.Width < 360, $"{panel.Bounds.Width}");
        Assert.NotNull(window.GetVisualDescendants().OfType<WeatherView>().SingleOrDefault());
        Assert.True(window.GetVisualDescendants().OfType<WeatherView>().Single().Bounds.Height < 150);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Control>(), c => c.Name is "TerrainStrip" or "TerrainCredit");
        var credits = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Credits");
        Assert.NotNull(ToolTip.GetTip(credits));

        app.Get<ShellViewModel>().Show(ShellViewModel.Scene3D);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Single(window.GetVisualDescendants().OfType<TelemetryPanelView>());
        Assert.Contains("Copernicus", (string)ToolTip.GetTip(window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Credits"))!, StringComparison.Ordinal);
        window.Close();
    }
}
