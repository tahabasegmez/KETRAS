using Ketras.Comms;
using Ketras.Controls;
using Ketras.Parameters;
using Ketras.Planning;
using Ketras.Scene3D;
using Ketras.Settings;
using Ketras.Setup;
using Ketras.Shell;
using Ketras.Simulation;

namespace Ketras;

/// <summary>Which view shows which ViewModel, for content areas that host a ViewModel.</summary>
public static class Views
{
    /// <summary>The view map.</summary>
    public static ViewLocator Create() => new ViewLocator()
        .Register<PendingActivityViewModel, PendingActivityView>()
        .Register<SettingsViewModel, SettingsView>()
        .Keep<PlanningViewModel, PlanningView>()
        .Keep<Scene3DViewModel, Scene3DView>()
        .Register<ConnectionViewModel, SplitScreenView>()
        .Register<LinkStatusPageViewModel, LinkStatusPageView>()
        .Register<LinksPageViewModel, LinksPageView>()
        .Register<SimulatorsPageViewModel, SimulatorsPageView>()
        .Register<SetupViewModel, SplitScreenView>()
        .Register<SetupBannerViewModel, SetupBannerView>()
        .Register<SummaryPageViewModel, SummaryPageView>()
        .Register<SensorsPageViewModel, SensorsPageView>()
        .Register<RadioPageViewModel, RadioPageView>()
        .Register<PowerPageViewModel, PowerPageView>()
        .Register<ActuatorsPageViewModel, ActuatorsPageView>()
        .Register<ParametersPageViewModel, ParametersPageView>();
}
