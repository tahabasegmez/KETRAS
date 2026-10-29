using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ketras.App.Settings;
using Ketras.App.Shell;
using Ketras.Station.Notifications;

namespace Ketras.Tests.Shell;

/// <summary>The shell's views on the headless platform: built, bound and laid out, driven through their controls.</summary>
[Collection(AvaloniaCollection.Name)]
public sealed class ViewTests
{
    private static MainWindow Show(AppFixture app)
    {
        var window = new MainWindow { DataContext = app.Get<ShellViewModel>() };
        window.Show();
        Pump(window);
        return window;
    }

    /// <summary>Runs queued work and a layout pass, as a frame would.</summary>
    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(control => control.Name == name);

    [AvaloniaFact]
    public async Task The_shell_shows_the_activity_bar_the_status_bar_and_the_selected_activity()
    {
        await using var app = AppFixture.Create();
        var window = Show(app);

        var activityButtons = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("activity")).ToList();
        Assert.Equal(5, activityButtons.Count);
        Assert.Single(activityButtons, b => b.Classes.Contains("active"));
        Assert.Equal("Not connected", Find<TextBlock>(window, "LinkText").Text);
        Assert.False(Find<Button>(window, "VehicleItem").IsVisible);
        Assert.False(Find<Button>(window, "ConditionsItem").IsVisible);
        Assert.NotNull(window.GetVisualDescendants().OfType<Ketras.App.Planning.PlanningView>().SingleOrDefault());
        Assert.True(window.Bounds.Width > 0);

        var settings = activityButtons.Single(b => b.CommandParameter == app.Get<ShellViewModel>().SettingsActivity);
        settings.Command!.Execute(settings.CommandParameter);
        Pump(window);

        Assert.Contains("active", settings.Classes);
        Assert.Single(activityButtons, b => b.Classes.Contains("active"));
        Assert.NotNull(window.GetVisualDescendants().OfType<SettingsView>().SingleOrDefault());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Coming_back_to_planning_shows_the_same_map_where_it_was_left()
    {
        await using var app = AppFixture.Create();
        var window = Show(app);
        var shell = app.Get<ShellViewModel>();
        var map = window.GetVisualDescendants().OfType<Controls.Map.MapView>().Single();
        map.CenterOn(new Model.Geo.GeoPoint(47.4, 8.5), 14);
        Pump(window);

        shell.Show(ShellViewModel.Scene3D);
        Pump(window);
        Assert.Empty(window.GetVisualDescendants().OfType<Controls.Map.MapView>());
        shell.Show(ShellViewModel.Settings);
        Pump(window);
        shell.Show(ShellViewModel.Planning);
        Pump(window);

        Assert.Same(map, window.GetVisualDescendants().OfType<Controls.Map.MapView>().Single());
        Assert.Same(app.Get<Ketras.App.Planning.PlanningViewModel>(), map.FindAncestorOfType<Ketras.App.Planning.PlanningView>()!.DataContext);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Events_become_toasts_and_conditions_show_in_the_status_bar()
    {
        await using var app = AppFixture.Create();
        var window = Show(app);
        var store = app.Get<NotificationStore>();

        store.Notify("Plan", NotificationSeverity.Warning, "Upload refused", "the plan is empty");
        store.SetCondition("Terrain", "ahead", NotificationSeverity.Warning, "Terrain ahead");
        store.SetCondition("Link", "lost", NotificationSeverity.Error, "Vehicle link lost");
        Pump(window);

        var toast = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("toast"));
        var texts = toast.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Equal(["Upload refused", "the plan is empty", "Source: Plan"], texts);
        Assert.True(Find<Button>(window, "ConditionsItem").IsVisible);
        Assert.Equal("2", Find<TextBlock>(window, "ConditionCount").Text);
        Assert.Equal("1", Find<TextBlock>(window, "UnreadCount").Text);

        var close = toast.GetVisualDescendants().OfType<Button>().Single();
        close.Command!.Execute(null);
        Pump(window);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("toast"));

        // The list: one 22 px line per condition, most severe first; pressing a line opens its source under it.
        var item = Find<Button>(window, "ConditionsItem");
        var flyout = (Flyout)item.Flyout!;
        flyout.ShowAt(item);
        Pump(window);
        var list = (Control)flyout.Content!;
        Assert.Equal(new Thickness(0), list.FindAncestorOfType<FlyoutPresenter>()!.Padding);
        var rows = list.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("notification")).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(22, rows[0].Bounds.Height);
        var details = list.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "Details");
        Assert.False(details.IsVisible);
        rows[0].Command!.Execute(null);
        Pump(window);
        Assert.True(details.IsVisible);
        Assert.Contains(details.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Source: Link");
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_history_opens_with_unread_rows_and_an_opened_row_shows_its_whole_title()
    {
        await using var app = AppFixture.Create();
        var window = Show(app);
        var store = app.Get<NotificationStore>();
        var title = "PX4: Preflight Fail: height estimate not stable, wait for the estimator to converge before arming";
        store.Notify("Vehicle 1", NotificationSeverity.Warning, title);
        Pump(window);

        // The unread fill covers the whole bell, not only its content.
        var bell = Find<Button>(window, "Bell");
        Assert.Equal(bell.Bounds.Height, Find<Border>(window, "BellFill").Bounds.Height);

        var flyout = (Flyout)bell.Flyout!;
        flyout.ShowAt(bell);
        Pump(window);
        var list = (Control)flyout.Content!;
        var row = list.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("notification-row"));
        Assert.Contains("unread", row.Classes);
        var header = row.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("notification"));
        Assert.Equal(22, header.Bounds.Height);

        header.Command!.Execute(null);
        Pump(window);
        var text = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("notification-title"));
        Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
        Assert.True(header.Bounds.Height > 22);
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_link_item_opens_the_vehicle_link_activity()
    {
        await using var app = AppFixture.Create();
        var window = Show(app);
        var shell = app.Get<ShellViewModel>();

        var link = Find<Button>(window, "LinkItem");
        link.Command!.Execute(null);
        Pump(window);

        Assert.Equal(ShellViewModel.Link, shell.SelectedActivity.Id);
        Assert.NotNull(window.GetVisualDescendants().OfType<Ketras.App.Common.SplitScreenView>().SingleOrDefault());
        window.Close();
    }
}
