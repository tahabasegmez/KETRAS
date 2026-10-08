using System.Reflection;
using Ketras.Mavlink;
using NetArchTest.Rules;

namespace Ketras.Architecture.Tests;

/// <summary>The dependency rules of docs/ARCHITECTURE.md, checked on the compiled assemblies.</summary>
public sealed class DependencyRules
{
    private static readonly string[] Ui = ["Avalonia", "Mapsui", "LibVLCSharp", "CommunityToolkit.Mvvm"];

    [Fact]
    public void Mavlink_depends_on_no_other_ketras_project_and_no_ui() =>
        AssertNoDependency(typeof(MavlinkConnection).Assembly, [.. OtherKetras("Ketras.Mavlink"), .. Ui]);

    [Fact]
    public void Mavlink_testing_depends_only_on_mavlink() =>
        AssertNoDependency(typeof(Mavlink.Testing.FakeVehicle).Assembly, [.. OtherKetras("Ketras.Mavlink"), .. Ui]);

    [Fact]
    public void Model_depends_on_nothing_but_the_runtime() =>
        AssertNoDependency(typeof(Model.Planning.Plan).Assembly, [.. OtherKetras("Ketras.Model"), .. Ui, "Microsoft.Extensions"]);

    [Fact]
    public void Rosbridge_depends_on_no_other_ketras_project_and_no_ui() =>
        AssertNoDependency(typeof(Rosbridge.RosDispatcher).Assembly, [.. OtherKetras("Ketras.Rosbridge"), .. Ui]);

    [Fact]
    public void Terrain_depends_on_no_other_ketras_project_and_no_ui() =>
        AssertNoDependency(typeof(Terrain.TerrainTileStore).Assembly, [.. OtherKetras("Ketras.Terrain"), .. Ui]);

    [Fact]
    public void Station_depends_only_on_the_libraries_below_it_and_no_ui() =>
        AssertNoDependency(typeof(Station.StationServiceCollectionExtensions).Assembly,
            ["Ketras.Mavlink.Testing", "Ketras.Theme", "Ketras.Controls", "Ketras.App", "Ketras.Plugins", "Ketras.Simulation", .. Ui]);

    [Fact]
    public void The_px4_sitl_simulator_depends_on_the_station_and_no_ui() =>
        AssertNoDependency(typeof(Simulation.Px4Sitl.Px4Sitl).Assembly,
            ["Ketras.Mavlink.Testing", "Ketras.Theme", "Ketras.Controls", "Ketras.App", "Ketras.Plugins", .. Ui]);

    [Fact]
    public void The_template_plugin_depends_on_the_station_and_no_ui() =>
        AssertNoDependency(typeof(Plugins.Template.TemplatePlugin).Assembly,
            ["Ketras.Mavlink.Testing", "Ketras.Theme", "Ketras.Controls", "Ketras.App", "Ketras.Simulation", .. Ui]);

    [Fact]
    public void Theme_depends_on_avalonia_only() =>
        AssertNoDependency(typeof(Theme.KetrasTheme).Assembly, [.. OtherKetras("Ketras.Theme"), "Mapsui", "LibVLCSharp", "CommunityToolkit.Mvvm"]);

    [Fact]
    public void Instruments_depend_on_avalonia_and_the_theme_only() =>
        AssertNoDependency(typeof(Controls.Instruments.AttitudeIndicator).Assembly,
            ["Ketras.Station", "Ketras.Mavlink", "Ketras.Rosbridge", "Ketras.Terrain", "Ketras.App", "Ketras.Plugins", "Ketras.Simulation", "CommunityToolkit.Mvvm", "Mapsui", "LibVLCSharp"]);

    [Fact]
    public void The_3d_view_depends_on_avalonia_the_theme_and_the_model_only() =>
        AssertNoDependency(typeof(Controls.Scene3D.AirspaceView).Assembly,
            ["Ketras.Station", "Ketras.Mavlink", "Ketras.Rosbridge", "Ketras.Terrain", "Ketras.App", "Ketras.Plugins", "Ketras.Simulation", "CommunityToolkit.Mvvm", "Mapsui", "LibVLCSharp"]);

    [Fact]
    public void The_map_depends_on_avalonia_the_theme_and_the_model_only() =>
        AssertNoDependency(typeof(Controls.Map.MapView).Assembly,
            ["Ketras.Station", "Ketras.Mavlink", "Ketras.Rosbridge", "Ketras.Terrain", "Ketras.App", "Ketras.Plugins", "Ketras.Simulation", "CommunityToolkit.Mvvm", "LibVLCSharp", "HelixToolkit"]);

    [Fact]
    public void ViewModels_use_no_avalonia_type()
    {
        var result = Types.InAssembly(typeof(App.Shell.ShellViewModel).Assembly)
            .That().HaveNameEndingWith("ViewModel")
            .ShouldNot().HaveDependencyOnAny("Avalonia", "Lucide.Avalonia")
            .GetResult();
        Assert.True(result.IsSuccessful, $"ViewModels using Avalonia: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    private static string[] OtherKetras(string allowedPrefix) =>
        new[] { "Ketras.Model", "Ketras.Station", "Ketras.Rosbridge", "Ketras.Terrain", "Ketras.Theme", "Ketras.Controls", "Ketras.App", "Ketras.Plugins", "Ketras.Simulation", "Ketras.Mavlink" }
            .Where(name => !name.StartsWith(allowedPrefix, StringComparison.Ordinal))
            .ToArray();

    private static void AssertNoDependency(Assembly assembly, string[] forbidden)
    {
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
        Assert.True(result.IsSuccessful, $"{assembly.GetName().Name} depends on a forbidden namespace: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
