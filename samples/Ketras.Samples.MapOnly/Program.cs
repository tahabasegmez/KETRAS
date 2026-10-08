// The Ketras map control on its own: no station, no application. A small plan is drawn on the map; the map's edit
// intents (place, move, draw a fence) are applied to the plan here, which is all a host has to do.
// Usage: dotnet run --project samples/Ketras.Samples.MapOnly
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Styling;
using Ketras.Controls.Map;
using Ketras.Model.Fences;
using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Model.Planning;
using Ketras.Theme;

AppBuilder.Configure<SampleApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);

/// <summary>An application with the Ketras theme and one window holding a <see cref="MapView"/>.</summary>
internal sealed class SampleApp : Application
{
    private static readonly GeoPoint Home = new(47.397971, 8.546164);

    private Plan _plan = Plan.Empty;
    private MapView _map = null!;

    public override void Initialize()
    {
        Styles.Add(new KetrasTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _plan = new Plan
        {
            Mission = new Mission
            {
                Settings = new MissionSettings { PlannedHome = Home, HomeAltitudeMeters = 488 },
                Items =
                [
                    MissionItemFactory.Create(MissionItemType.Takeoff, GeoMath.Offset(Home, 0, 300)),
                    MissionItemFactory.Create(MissionItemType.Waypoint, GeoMath.Offset(Home, 30, 1200)),
                    MissionItemFactory.Create(MissionItemType.Waypoint, GeoMath.Offset(Home, 90, 1500)),
                    MissionItemFactory.Create(MissionItemType.Land, GeoMath.Offset(Home, 180, 200))
                ]
            },
            Fences = [new Fence("F1", new CircleArea(Home, 2500), Inclusion: true)]
        };

        _map = new MapView
        {
            Plan = _plan,
            PlaceItemCommand = new Command<GeoPoint>(point => Apply(MissionEditor.Add(_plan.Mission, MissionItemType.Waypoint, point).Mission)),
            MoveItemCommand = new Command<ItemMove>(move => Apply(MissionEditor.Update(_plan.Mission, move.ItemId, item => item with { Location = move.Location }).Mission)),
            AddFenceCommand = new Command<Area>(area => Apply(_plan with { Fences = _plan.Fences.Add(new Fence(Fence.NextId(_plan.Fences), area)) })),
            EditFenceCommand = new Command<FenceEdit>(edit => Apply(_plan with
            {
                Fences = [.. _plan.Fences.Select(f => f.Id == edit.FenceId ? f with { Area = edit.Area } : f)]
            }))
        };

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        tools.Children.Add(ToolButton("Add waypoint", MapTool.AddItem));
        tools.Children.Add(ToolButton("Draw polygon (right click closes)", MapTool.DrawPolygon));
        tools.Children.Add(ToolButton("Drop circle", MapTool.DrawCircle));
        var street = new Button { Content = "Satellite / street" };
        street.Click += (_, _) => _map.BaseMap = _map.BaseMap == BaseMapKind.Satellite ? BaseMapKind.Street : BaseMapKind.Satellite;
        tools.Children.Add(street);

        var window = new Window { Title = "Ketras map sample", Width = 1100, Height = 750, Content = new Panel { Children = { _map, tools } } };
        window.Opened += (_, _) => _map.FitToPlan();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private Button ToolButton(string label, MapTool tool)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) => _map.Tool = tool;
        return button;
    }

    private void Apply(Mission mission) => Apply(_plan with { Mission = mission });

    private void Apply(Plan plan)
    {
        _plan = plan;
        _map.Plan = plan;
    }
}

/// <summary>An always-enabled command for one parameter type.</summary>
internal sealed class Command<T>(Action<T> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => parameter is T;

    public void Execute(object? parameter) => execute((T)parameter!);
}
