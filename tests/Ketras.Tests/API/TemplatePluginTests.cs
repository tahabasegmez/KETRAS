using System.Text.Json;
using System.Text.Json.Nodes;
using Ketras.Plugins.Template;
using Ketras.Station;
using Ketras.Station.Commands;
using Ketras.Station.Notifications;
using Ketras.Station.Plugins;
using Ketras.Station.Ros;

namespace Ketras.Tests.API;

public sealed class TemplatePluginTests
{
    [Fact]
    public async Task The_template_plugin_publishes_calls_and_reports_through_rosbridge_with_the_namespace()
    {
        await using var rosbridge = new FakeRosbridge(accepting: false);
        rosbridge.Services["/uav1/template/lights"] = new JsonObject { ["success"] = true, ["message"] = "on" };
        await using var station = await StationFixture.StartAsync(options =>
        {
            options.Ros.Enabled = true;
            options.Ros.Url = rosbridge.Uri.ToString();
            options.Ros.NamespacePrefix = "/uav1";
            options.Plugins.Settings[TemplatePlugin.PluginName] = JsonDocument.Parse("""{"helloText":"hi there"}""").RootElement;
        }, services => services.AddStationPlugin<TemplatePlugin>());
        var commands = station.Get<CommandService>();
        VehicleCommand Command(string id) => commands.Commands.Single(c => c.Id == id);
        Assert.Equal("not connected to rosbridge", commands.WhyNotRunnable(Command("template.hello")));
        rosbridge.Accept();
        await StationFixture.WaitForAsync(() => station.Get<RosService>().Dispatcher!.IsConnected);

        Assert.Null(commands.WhyNotRunnable(Command("template.hello")));
        Assert.Equal("no status received yet", (await commands.ExecuteAsync(new CommandRequest(Command("template.report")))).Message);
        await station.ConnectAsync();

        var hello = await commands.ExecuteAsync(new CommandRequest(Command("template.hello")));
        var lights = await commands.ExecuteAsync(new CommandRequest(Command("template.lights"), new Dictionary<string, string> { ["data"] = "true" }));
        await StationFixture.WaitForAsync(() => rosbridge.Ops("publish").Any());
        await rosbridge.PublishAsync("/uav1/template/status", new JsonObject { ["data"] = "payload ready" });
        await StationFixture.WaitForAsync(async () => (await commands.ExecuteAsync(new CommandRequest(Command("template.report")))).Message == "last status: payload ready");

        Assert.True(hello.IsAccepted, hello.Message);
        Assert.True(lights.IsAccepted, lights.Message);
        var published = rosbridge.Ops("publish").Single();
        Assert.Equal(("/uav1/template/hello", "hi there"), ((string?)published["topic"], (string?)published["msg"]!["data"]));
        Assert.Contains(rosbridge.Ops("subscribe"), op => (string?)op["topic"] == "/uav1/template/status");
        var call = rosbridge.Ops("call_service").Single(op => (string?)op["service"] == "/uav1/template/lights");
        Assert.True(call["args"]!["data"]!.GetValue<bool>());

        await commands.ExecuteAsync(new CommandRequest(Command("arm")));
        await StationFixture.WaitForAsync(() => commands.WhyNotRunnable(Command("template.lights")) is not null);
    }

    [Fact]
    public async Task Without_ros_the_template_plugin_does_not_start_and_says_why()
    {
        await using var station = await StationFixture.StartAsync(register: services => services.AddStationPlugin<TemplatePlugin>());

        Assert.Empty(station.Get<PluginHost>().Plugins);
        Assert.DoesNotContain(station.Get<CommandService>().Commands, c => c.Id.StartsWith("template.", StringComparison.Ordinal));
        Assert.Contains(station.Get<NotificationStore>().History, n => n.Title == "Plugin template did not start" && n.Detail!.Contains("Ketras:Ros:Enabled", StringComparison.Ordinal));
    }
}
