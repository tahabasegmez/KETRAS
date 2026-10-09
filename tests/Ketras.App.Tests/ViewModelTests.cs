using Ketras.App.Common;
using Ketras.App.Logging;
using Ketras.App.Notifications;
using Ketras.App.Settings;
using Ketras.App.Shell;
using Ketras.Station.Links;
using Ketras.Station.Notifications;
using Ketras.Station.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ketras.App.Tests;

public sealed class ViewModelTests
{
    [Fact]
    public void Events_are_unread_until_the_history_opens_and_conditions_never_are()
    {
        var store = new NotificationStore(TimeProvider.System);
        using var center = new NotificationCenterViewModel(store, new InlineUiDispatcher());

        store.Notify("Plan", NotificationSeverity.Info, "saved");
        store.Notify("Link", NotificationSeverity.Error, "lost");
        store.SetCondition("Terrain", "ahead", NotificationSeverity.Warning, "terrain ahead");
        store.SetCondition("Link", "lost", NotificationSeverity.Error, "no link");

        Assert.Equal((2, NotificationSeverity.Error), (center.UnreadCount, center.UnreadSeverity));
        Assert.Equal(["no link", "terrain ahead"], center.Conditions.Select(c => c.Title));
        Assert.Equal(NotificationSeverity.Error, center.ConditionSeverity);
        Assert.Equal(4, center.History.Count);
        Assert.Equal(2, center.History.Count(row => row.IsUnread));

        // Opened: read at once, the rows unread before keep their tint until it closes; what arrives meanwhile is read.
        center.MarkReadCommand.Execute(null);
        Assert.False(center.HasUnread);
        Assert.Equal(2, center.History.Count(row => row.IsUnread));
        center.History[0].ToggleCommand.Execute(null);
        var opened = center.History[0];
        store.Notify("Vehicle 1", NotificationSeverity.Warning, "Preflight Fail: no GPS");
        Assert.False(center.HasUnread);
        Assert.False(center.History[0].IsUnread);
        Assert.True(opened.IsExpanded);
        Assert.Same(opened, center.History[1]);
        center.HistoryClosedCommand.Execute(null);
        Assert.DoesNotContain(center.History, row => row.IsUnread);

        center.Conditions[0].ToggleCommand.Execute(null);
        store.SetCondition("Link", "lost", NotificationSeverity.Error, "no link", "still nothing");
        Assert.True(center.Conditions[0].IsExpanded);
        Assert.Equal("still nothing", center.Conditions[0].Detail);

        center.OnlyProblems = true;
        Assert.Equal(["Preflight Fail: no GPS", "no link", "terrain ahead", "lost"], center.History.Select(row => row.Title));

        store.ClearCondition("Link", "lost");
        center.ClearAllCommand.Execute(null);
        Assert.True(center.IsHistoryEmpty);
        Assert.Equal("terrain ahead", Assert.Single(center.Conditions).Title);
    }

    [Fact]
    public void Toasts_show_events_only_at_most_three_and_leave_after_their_severitys_time()
    {
        var time = new ManualTime();
        var store = new NotificationStore(time);
        using var toasts = new ToastsViewModel(store, new InlineUiDispatcher(), time);

        store.SetCondition("Terrain", "ahead", NotificationSeverity.Warning, "terrain ahead");
        store.Notify("A", NotificationSeverity.Error, "one");
        store.Notify("B", NotificationSeverity.Success, "two");
        store.Notify("C", NotificationSeverity.Info, "three");
        store.Notify("D", NotificationSeverity.Warning, "four");

        Assert.Equal(["two", "three", "four"], toasts.Toasts.Select(t => t.Row.Title));
        time.Advance(TimeSpan.FromSeconds(3.5));
        Assert.Equal(["three", "four"], toasts.Toasts.Select(t => t.Row.Title));
        toasts.Toasts[1].DismissCommand.Execute(null);
        Assert.Equal("three", Assert.Single(toasts.Toasts).Row.Title);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(toasts.Toasts);
        Assert.Equal("Source: C", store.History.Select(n => new NotificationRowViewModel(n)).Single(r => r.Title == "three").Source);
    }

    [Fact]
    public async Task The_link_item_follows_the_connection_and_opens_the_link_activity()
    {
        await using var app = AppFixture.Create();
        await app.StartAsync();
        var shell = app.Get<ShellViewModel>();
        var link = shell.LinkStatus;

        Assert.Equal(("Not connected", LinkState.Disconnected), (link.Text, link.State));
        await app.Get<ConnectionService>().ConnectAsync(new LinkProfile { Name = "Bench", Kind = LinkKind.Udp });
        await AppFixture.WaitForAsync(() => link.IsConnected && shell.VehicleStatus.HasVehicle);

        Assert.Equal("Bench", link.Text);
        Assert.Contains("udp://:14550", link.Tip, StringComparison.Ordinal);
        Assert.Equal("Vehicle 1", shell.VehicleStatus.ActiveName);
        Assert.False(shell.VehicleStatus.CanSwitch);

        app.Vehicle.HeartbeatPaused = true;
        await AppFixture.WaitForAsync(() => link.IsLost);
        Assert.Equal("Link lost", link.Text);

        link.OpenCommand.Execute(null);
        Assert.Equal(ShellViewModel.Link, shell.SelectedActivity.Id);
    }

    [Fact]
    public async Task The_shell_selects_one_activity_at_a_time()
    {
        await using var app = AppFixture.Create();
        var shell = app.Get<ShellViewModel>();

        Assert.Equal(ShellViewModel.Planning, shell.SelectedActivity.Id);
        Assert.True(shell.Activities[0].IsSelected);
        shell.Show(ShellViewModel.Settings);
        shell.Show("nothing");

        Assert.True(shell.IsSettingsSelected);
        Assert.IsType<SettingsViewModel>(shell.SelectedActivity.Content);
        Assert.False(shell.Activities[0].IsSelected);
        Assert.Equal(["planning", "scene3d", "setup", "link"], shell.Activities.Select(a => a.Id));
    }

    [Fact]
    public async Task The_settings_page_names_the_folders_and_the_license()
    {
        await using var app = AppFixture.Create();

        var reopened = new SettingsViewModel(new SettingsStore(app.Get<StationPaths>(), NullLogger<SettingsStore>.Instance), app.Get<StationPaths>(), new UnitService());
        Assert.Equal(app.Get<StationPaths>().DataDirectory, reopened.DataFolder);
        Assert.Equal("GPL-3.0-or-later", reopened.License);
    }

    [Fact]
    public async Task The_terrain_download_mode_is_automatic_until_manual_is_chosen_and_stays_chosen()
    {
        await using var app = AppFixture.Create();
        var settings = app.Get<SettingsViewModel>();
        Assert.Equal(Station.Terrain.TerrainDownloadMode.Auto, settings.SelectedTerrainMode.Value);

        settings.SelectedTerrainMode = settings.TerrainModes.Single(m => m.Value == Station.Terrain.TerrainDownloadMode.Manual);

        Assert.Equal("Manual", app.Get<SettingsStore>().Get(Station.Terrain.TerrainService.DownloadModeSetting));
        var reopened = new SettingsViewModel(new SettingsStore(app.Get<StationPaths>(), NullLogger<SettingsStore>.Instance), app.Get<StationPaths>(), new UnitService());
        Assert.Equal(Station.Terrain.TerrainDownloadMode.Manual, reopened.SelectedTerrainMode.Value);
    }

    [Fact]
    public void Units_are_formatted_in_metric()
    {
        var units = new UnitService();

        Assert.Equal(UnitSystem.Metric, units.System);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:F0} m", 850.0), units.Distance(850));
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:F2} km", 12.4), units.Distance(12_400));
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:F1} m/s", 18.5), units.Speed(18.5));
    }

    [Fact]
    public void The_file_log_writes_one_file_per_day_and_keeps_two_weeks()
    {
        var directory = Directory.CreateTempSubdirectory("ketras-log-").FullName;
        for (var day = 1; day <= 16; day++)
        {
            File.WriteAllText(Path.Combine(directory, $"ketras-202609{day:00}.log"), "old");
        }

        using (var provider = new FileLoggerProvider(directory))
        {
            provider.CreateLogger("Test").LogWarning("written {Value}", 42);
        }

        var files = Directory.GetFiles(directory, "ketras-*.log");
        var today = Path.Combine(directory, $"ketras-{DateTime.Now:yyyyMMdd}.log");
        Assert.Contains("warn Test: written 42", File.ReadAllText(today), StringComparison.Ordinal);
        Assert.Equal(FileLoggerProvider.KeepDays + 1, files.Length);
        Directory.Delete(directory, recursive: true);
    }
}
