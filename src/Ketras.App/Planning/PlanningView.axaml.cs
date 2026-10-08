using System.ComponentModel;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Ketras.Controls.Map;

namespace Ketras.App.Planning;

/// <summary>
/// View of <see cref="PlanningViewModel"/>: hands the aircraft feed and fit requests to the map, and animates the
/// planning panel as the KetrasPlanner control did: the switch's highlight slides to the chosen half and the chosen
/// editor slides in from its side (mission sits left of geofence) while the other slides out.
/// </summary>
public sealed partial class PlanningView : UserControl
{
    private static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(180);

    private PlanningViewModel? _model;

    /// <summary>Creates the view.</summary>
    public PlanningView()
    {
        InitializeComponent();
        foreach (var editor in new Control[] { MissionPanel, FencePanel })
        {
            editor.Transitions =
            [
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = SlideTime, Easing = new CubicEaseOut() },
                new DoubleTransition { Property = OpacityProperty, Duration = SlideTime }
            ];
        }

        SwitchGrid.SizeChanged += (_, _) => PlaceHighlight(animate: false);
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.AircraftUpdated -= OnAircraft;
            _model.FitRequested -= OnFit;
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = DataContext as PlanningViewModel;
        if (_model is not null)
        {
            _model.AircraftUpdated += OnAircraft;
            _model.FitRequested += OnFit;
            _model.PropertyChanged += OnModelChanged;
            MissionPanel.IsVisible = _model.IsMissionPanel;
            FencePanel.IsVisible = _model.IsGeofencePanel;
            PlaceHighlight(animate: false);
        }
    }

    private void OnAircraft(MapAircraft aircraft) => Map.PushAircraft(aircraft);

    private void OnFit() => Map.FitToPlan();

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlanningViewModel.IsGeofencePanel) && _model is not null)
        {
            PlaceHighlight(animate: true);
            ShowEditor(_model.IsGeofencePanel);
        }
    }

    private void PlaceHighlight(bool animate)
    {
        var width = SwitchGrid.Bounds.Width / 2;
        SwitchHighlight.Width = width > 0 ? width : double.NaN;
        SwitchHighlight.Transitions = animate
            ? [new TransformOperationsTransition { Property = RenderTransformProperty, Duration = SlideTime, Easing = new CubicEaseOut() }]
            : null;
        var index = _model?.IsGeofencePanel == true ? 1 : 0;
        SwitchHighlight.RenderTransform = TransformOperations.Parse(FormattableString.Invariant($"translateX({index * width}px)"));
    }

    private void ShowEditor(bool geofence)
    {
        var width = PlanningContent.Bounds.Width + 20;
        Control incoming = geofence ? FencePanel : MissionPanel;
        Control outgoing = geofence ? MissionPanel : FencePanel;

        incoming.IsVisible = true;
        incoming.IsHitTestVisible = true;
        incoming.Opacity = 1;
        incoming.RenderTransform = TransformOperations.Parse("translateX(0px)");

        outgoing.IsHitTestVisible = false;
        outgoing.Opacity = 0;
        outgoing.RenderTransform = TransformOperations.Parse(FormattableString.Invariant($"translateX({(geofence ? -width : width)}px)"));

        // Hidden once off screen, so the panel takes the height of the editor shown.
        DispatcherTimer.RunOnce(() =>
        {
            if (_model?.IsGeofencePanel == geofence)
            {
                outgoing.IsVisible = false;
            }
        }, SlideTime);
    }
}
