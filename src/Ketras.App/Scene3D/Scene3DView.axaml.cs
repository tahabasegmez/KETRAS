using System.ComponentModel;
using Avalonia.Controls;

namespace Ketras.App.Scene3D;

/// <summary>
/// View of <see cref="Scene3DViewModel"/>. The scene control takes method calls rather than bindings, so this view
/// adapts: every change of the ViewModel goes to the scene through <see cref="SceneMapper"/>.
/// </summary>
public sealed partial class Scene3DView : UserControl
{
    private Scene3DViewModel? _model;

    /// <summary>Creates the view.</summary>
    public Scene3DView() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.FitRequested -= Scene.FitAll;
            Scene.FollowReleased -= _model.ReleaseFollow;
            Scene.ImageryDetailRequested -= _model.RequestImageryDetail;
        }

        _model = DataContext as Scene3DViewModel;
        if (_model is null)
        {
            return;
        }

        _model.PropertyChanged += OnModelChanged;
        _model.FitRequested += Scene.FitAll;
        Scene.FollowReleased += _model.ReleaseFollow;
        Scene.ImageryDetailRequested += _model.RequestImageryDetail;
        foreach (var property in new[] { nameof(_model.Plan), nameof(_model.Ownship), nameof(_model.Traffic), nameof(_model.Points), nameof(_model.Terrain), nameof(_model.Followed), nameof(_model.Surface), nameof(_model.Imagery), nameof(_model.ImageryDetail) })
        {
            Apply(_model, property);
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_model is not null && e.PropertyName is { } property)
        {
            Apply(_model, property);
        }
    }

    private void Apply(Scene3DViewModel model, string property)
    {
        switch (property)
        {
            case nameof(Scene3DViewModel.Plan):
                Scene.SetOptions(SceneMapper.Options(model.Plan));
                Scene.SetPath(SceneMapper.Path(model.Plan, model.GroundAt));
                Scene.SetVolumes(SceneMapper.Volumes(model.Plan, model.Zones));
                break;
            case nameof(Scene3DViewModel.Zones):
                Scene.SetVolumes(SceneMapper.Volumes(model.Plan, model.Zones));
                break;
            case nameof(Scene3DViewModel.Ownship):
                Scene.SetOwnship(SceneMapper.Ownship(model.Ownship));
                break;
            case nameof(Scene3DViewModel.Traffic):
                Scene.SetTraffic(SceneMapper.Traffic(model.Traffic, model.Plan.Mission.Settings.HomeAltitudeMeters));
                break;
            case nameof(Scene3DViewModel.Points):
                Scene.SetPoints(SceneMapper.Points(model.Points));
                break;
            case nameof(Scene3DViewModel.Terrain):
                Scene.SetTerrain(model.Terrain);
                break;
            case nameof(Scene3DViewModel.Surface):
                Scene.SetGround(SceneMapper.Ground(model.Surface));
                break;
            case nameof(Scene3DViewModel.Imagery):
                Scene.SetImagery(SceneMapper.Imagery(model.Imagery));
                break;
            case nameof(Scene3DViewModel.ImageryDetail):
                Scene.SetImageryDetail(SceneMapper.Imagery(model.ImageryDetail));
                break;
            case nameof(Scene3DViewModel.Followed):
                Scene.Follow(model.Followed);
                break;
        }
    }
}
