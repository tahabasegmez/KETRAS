using CommunityToolkit.Mvvm.ComponentModel;
using Ketras.App.Commands;
using Ketras.App.Common;
using Ketras.App.Connection;
using Ketras.App.Notifications;
using Ketras.App.Planning;
using Ketras.App.Scene3D;
using Ketras.App.Resources;
using Ketras.App.Settings;
using Ketras.App.Setup;

namespace Ketras.App.Shell;

/// <summary>
/// The main window: the activity bar and the screen it selects, the status bar items and the toasts. One activity
/// fills the work area at a time; toasts float over whichever is shown.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    /// <summary>Activity ids.</summary>
    public const string Planning = "planning", Scene3D = "scene3d", Setup = "setup", Link = "link", Settings = "settings";

    /// <summary>Builds the shell from its parts.</summary>
    public ShellViewModel(LinkStatusViewModel link, VehicleStatusViewModel vehicle, FlightBarViewModel flight, NotificationCenterViewModel notifications,
        ToastsViewModel toasts, CommandBarViewModel commandBar, PlanningViewModel planning, Scene3DViewModel scene3D, ConnectionViewModel connection, SetupViewModel setup, SettingsViewModel settings)
    {
        CommandBar = commandBar;
        LinkStatus = link;
        VehicleStatus = vehicle;
        FlightBar = flight;
        NotificationCenter = notifications;
        Toasts = toasts;
        Activities =
        [
            new ActivityViewModel(Planning, Strings.ActivityPlanning, "Map", planning),
            new ActivityViewModel(Scene3D, Strings.ActivityScene3D, "Box", scene3D),
            new ActivityViewModel(Setup, Strings.ActivitySetup, "Settings2", setup),
            new ActivityViewModel(Link, Strings.ActivityLink, "RadioTower", connection)
        ];
        SettingsActivity = new ActivityViewModel(Settings, Strings.ActivitySettings, "Settings", settings);
        SelectedActivity = Activities[0];
        link.OpenRequested += () => Show(Link);
    }

    /// <summary>The activities at the top of the activity bar.</summary>
    public IReadOnlyList<ActivityViewModel> Activities { get; }

    /// <summary>The settings activity, at the bottom of the activity bar.</summary>
    public ActivityViewModel SettingsActivity { get; }

    /// <summary>The activity filling the work area.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsSelected))]
    public partial ActivityViewModel SelectedActivity { get; set; }

    /// <summary>True while the settings activity is shown.</summary>
    public bool IsSettingsSelected => SelectedActivity == SettingsActivity;

    /// <summary>The window title.</summary>
    public string Title => Strings.AppTitle;

    /// <summary>The command bar.</summary>
    public CommandBarViewModel CommandBar { get; }

    /// <summary>The vehicle link item.</summary>
    public LinkStatusViewModel LinkStatus { get; }

    /// <summary>The active vehicle item.</summary>
    public VehicleStatusViewModel VehicleStatus { get; }

    /// <summary>The GPS, link and flight time items.</summary>
    public FlightBarViewModel FlightBar { get; }

    /// <summary>The conditions and the bell.</summary>
    public NotificationCenterViewModel NotificationCenter { get; }

    /// <summary>The toasts.</summary>
    public ToastsViewModel Toasts { get; }

    /// <summary>Shows an activity by id; an unknown id changes nothing.</summary>
    public void Show(string id)
    {
        if (Activities.Append(SettingsActivity).FirstOrDefault(a => a.Id == id) is { } activity)
        {
            SelectedActivity = activity;
        }
    }

    /// <summary>Shows an activity.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Select(ActivityViewModel activity) => SelectedActivity = activity;

    partial void OnSelectedActivityChanged(ActivityViewModel oldValue, ActivityViewModel newValue)
    {
        // Null on the first selection, whatever the declared type says.
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
            (oldValue.Content as ILeaveAware)?.OnLeft();
        }

        newValue.IsSelected = true;
    }
}
