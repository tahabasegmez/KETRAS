using System.Text.Json;
using Ketras.API;
using Ketras.Commands;
using Ketras.Environment;
using Ketras.Geo;
using Ketras.Notifications;
using Ketras.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ketras.Tests.API;

public sealed class PluginAndStoreTests
{
    [Fact]
    public async Task A_plugin_gets_its_settings_and_adds_commands_and_environment_data()
    {
        var plugin = new GreetingPlugin();
        await using (var station = await StationFixture.StartAsync(
            options => options.Plugins.Settings["Greeting"] = JsonDocument.Parse("""{"text":"hello"}""").RootElement,
            services => services.AddSingleton<IStationPlugin>(plugin)))
        {
            var commands = station.Get<CommandService>();

            Assert.Equal("hello", plugin.Text);
            Assert.Same(plugin, Assert.Single(station.Get<PluginHost>().Plugins));
            Assert.Equal("hello", (await commands.ExecuteAsync(new CommandRequest(commands.Commands.Single(c => c.Id == "greet")))).Message);
            Assert.Equal("Greeting", Assert.Single(station.Get<EnvironmentStore>().Zones).Source);
        }

        Assert.True(plugin.Disposed);
    }

    [Fact]
    public async Task A_plugin_that_fails_to_start_is_reported_and_left_out()
    {
        var broken = new BrokenPlugin();
        await using var station = await StationFixture.StartAsync(register: services => services.AddSingleton<IStationPlugin>(broken));

        Assert.Empty(station.Get<PluginHost>().Plugins);
        Assert.True(broken.Disposed);
        var notification = Assert.Single(station.Get<NotificationStore>().History, n => n.Source == "Plugins");
        Assert.Equal(("Plugin Broken did not start", "no server"), (notification.Title, notification.Detail));
    }

    [Fact]
    public void Conditions_are_keyed_updated_in_place_and_recorded_once()
    {
        var store = new NotificationStore(TimeProvider.System);
        var raised = 0;
        var withdrawn = new List<string>();
        store.Raised += _ => raised++;
        store.Withdrawn += (_, key) => withdrawn.Add(key);

        store.SetCondition("Plan", "a", NotificationSeverity.Warning, "A");
        store.SetCondition("Plan", "a", NotificationSeverity.Warning, "A");
        store.SetCondition("Plan", "a", NotificationSeverity.Error, "A worse");
        store.SetCondition("Plan", "b", NotificationSeverity.Warning, "B");
        store.SetCondition("Link", "lost", NotificationSeverity.Error, "Lost");
        store.ClearConditionsExcept("Plan", new HashSet<string> { "b" });
        store.ClearCondition("Plan", "missing");

        Assert.Equal(4, raised);
        Assert.Equal(["a"], withdrawn);
        Assert.Equal(["B", "Lost"], store.Conditions.Select(c => c.Title).Order());
        Assert.Equal(["Lost", "B", "A"], store.History.Select(n => n.Title));

        for (var i = 0; i < NotificationStore.HistoryLimit + 10; i++)
        {
            store.Notify("Test", NotificationSeverity.Info, $"event {i}");
        }

        Assert.Equal(NotificationStore.HistoryLimit, store.History.Count);
        Assert.Equal($"event {NotificationStore.HistoryLimit + 9}", store.History[0].Title);
        store.ClearHistory();
        Assert.Empty(store.History);
        Assert.Equal(2, store.Conditions.Count);
    }

    [Fact]
    public void Settings_persist_and_fall_back_to_their_default_when_unreadable()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("ketras-settings-").FullName;
        var paths = new StationPaths(Path.Combine(directory, "config"), Path.Combine(directory, "data"));
        var style = new SettingDefinition<string>("instruments.style", "modern");
        var size = new SettingDefinition<int>("ui.size", 3);
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        var changed = new List<string>();
        store.Changed += changed.Add;

        Assert.Equal("modern", store.Get(style));
        store.Set(style, "classic");
        store.Set(new SettingDefinition<string>("ui.size", ""), "large");

        var reopened = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        Assert.Equal("classic", reopened.Get(style));
        Assert.Equal(3, reopened.Get(size));
        Assert.Equal(["instruments.style", "ui.size"], changed);

        File.WriteAllText(paths.ConfigFile("settings.json"), "{ not json");
        Assert.Equal("modern", new SettingsStore(paths, NullLogger<SettingsStore>.Instance).Get(style));
        System.IO.Directory.Delete(directory, recursive: true);
    }

    private sealed class GreetingPlugin : IStationPlugin, ICommandSource, IEnvironmentProvider
    {
        public string Name => "Greeting";

        public string? Text { get; private set; }

        public bool Disposed { get; private set; }

        public IReadOnlyList<VehicleCommand> Commands { get; } = [new VehicleCommand { Id = "greet", Label = "Greet" }];

        public event Action? CommandsChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(PluginContext context, CancellationToken cancellation)
        {
            Text = context.Settings.GetProperty("text").GetString();
            return Task.CompletedTask;
        }

        public Task<CommandResult?> TryHandleAsync(CommandRequest request, CancellationToken cancellation) =>
            Task.FromResult<CommandResult?>(request.Command.Id == "greet" ? CommandResult.Accepted(Text ?? "") : null);

        public Task StartAsync(IEnvironmentSink sink, CancellationToken cancellation)
        {
            sink.SetZones(Name, [new Zone { Id = "z", Source = Name, Area = new CircleArea(new GeoPoint(47, 8), 50) }]);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellation) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BrokenPlugin : IStationPlugin
    {
        public string Name => "Broken";

        public bool Disposed { get; private set; }

        public Task StartAsync(PluginContext context, CancellationToken cancellation) => throw new InvalidOperationException("no server");

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
