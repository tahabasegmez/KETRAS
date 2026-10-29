using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Map;
using Ketras.Model.Fences;
using Ketras.Model.Geo;
using Ketras.Parameters;
using Ketras.Planning.Missions;
using Ketras.Resources;
using Ketras.Setup;
using Ketras.Utilities;

namespace Ketras.Planning.Fences;

/// <summary>One fence as a list row: its shape icon and id in the fence's colour, "keep out · circle 300 m", and while selected its type and a circle's radius.</summary>
public sealed partial class FenceRowViewModel : ObservableObject
{
    private readonly PlanStore _plans;
    private readonly Action<string?> _select;

    /// <summary>Builds the row of <paramref name="fence"/>.</summary>
    public FenceRowViewModel(Fence fence, PlanStore plans, Action<string?> select, IUiDispatcher ui, TimeProvider time)
    {
        _plans = plans;
        _select = select;
        Fence = fence;
        Kind = new FieldViewModel("fence-kind", Strings.PlanningFenceType,
            [new FieldChoice(0, Strings.PlanningKeepOut), new FieldChoice(1, Strings.PlanningStayIn)], fence.Inclusion ? 1 : 0,
            value => Update(f => f with { Inclusion = value == 1 }), ui, time);
        Radius = fence.Area is CircleArea circle
            ? new FieldViewModel("radius", MissionLabels.Parameter("radius"), "m", circle.RadiusMeters, 0, 1, 100_000,
                value => Update(f => f.Area is CircleArea c ? f with { Area = c with { RadiusMeters = value } } : f), ui, time)
            : null;
    }

    /// <summary>The fence as the plan has it now.</summary>
    public Fence Fence { get; private set; }

    /// <summary>Its id.</summary>
    public string Id => Fence.Id;

    /// <summary>"Circle" or "Pentagon".</summary>
    public string Icon => Fence.Area is CircleArea ? "Circle" : "Pentagon";

    /// <summary>"keep out · circle 300 m" or "stay in · 5 corners".</summary>
    public string Summary => (Fence.Inclusion ? Strings.PlanningStayIn : Strings.PlanningKeepOut).ToLower(CultureInfo.CurrentCulture) + " · " + (Fence.Area switch
    {
        CircleArea circle => string.Format(CultureInfo.CurrentCulture, Strings.PlanningCircleSummary, circle.RadiusMeters),
        PolygonArea polygon => string.Format(CultureInfo.CurrentCulture, Strings.PlanningCorners, polygon.Vertices.Length),
        _ => string.Empty
    });

    /// <summary>It keeps the vehicle inside.</summary>
    public bool IsStayIn => Fence.Inclusion;

    /// <summary>Keep out or stay in.</summary>
    public FieldViewModel Kind { get; }

    /// <summary>A circle's radius; null for a polygon.</summary>
    public FieldViewModel? Radius { get; }

    /// <summary>It is a circle.</summary>
    public bool IsCircle => Radius is not null;

    /// <summary>It is selected (its fields show).</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>Takes the plan's current state of the fence.</summary>
    public void Apply(Fence fence)
    {
        Fence = fence;
        Kind.Apply(fence.Inclusion ? 1 : 0);
        if (fence.Area is CircleArea circle)
        {
            Radius?.Apply(circle.RadiusMeters);
        }

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsStayIn));
    }

    /// <summary>Selects the row, or clears the selection when it is selected already.</summary>
    [RelayCommand]
    private void Select() => _select(IsSelected ? null : Id);

    [RelayCommand]
    private void Delete() => _plans.RemoveFence(Id);

    private bool Update(Func<Fence, Fence> change)
    {
        _plans.UpsertFence(change(Fence));
        return true;
    }
}

/// <summary>
/// The geofence editor of the planning panel: the polygon and circle tools (armed while their map tool waits; a second
/// press disarms), the fences, clear, and the vehicle's breach settings (staged like every parameter edit, with their
/// own apply bar).
/// </summary>
public sealed partial class FencePanelViewModel : ObservableObject
{
    private readonly PlanStore _plans;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly Action<MapTool> _toggleTool;
    private readonly Action<string?> _select;
    private readonly Dictionary<string, FenceRowViewModel> _rows = [];

    /// <summary>Edits the fences in <paramref name="plans"/>; <paramref name="toggleTool"/> arms a map tool, or disarms it when armed.</summary>
    public FencePanelViewModel(PlanStore plans, SetupContext setup, IUiDispatcher ui, TimeProvider time, Action<MapTool> toggleTool, Action<string?> select)
    {
        Breach = new ParameterSectionViewModel(Strings.PlanningBreachSettings, [.. Px4ParameterGroups.Geofence.Select(name => new ParameterFieldViewModel(name, setup))]);
        Pending = new SetupBannerViewModel(setup, _ => { });
        setup.ValuesChanged += _ => Breach.Refresh();
        setup.StateChanged += () => Breach.Refresh();
        Breach.Refresh();
        _plans = plans;
        _ui = ui;
        _time = time;
        _toggleTool = toggleTool;
        _select = select;
    }

    /// <summary>The vehicle's geofence breach settings; hidden without a vehicle.</summary>
    public ParameterSectionViewModel Breach { get; }

    /// <summary>The pending parameter changes and Apply.</summary>
    public SetupBannerViewModel Pending { get; }

    /// <summary>The fences.</summary>
    public ObservableCollection<FenceRowViewModel> Rows { get; } = [];

    /// <summary>There are no fences.</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>The polygon tool waits for corners.</summary>
    [ObservableProperty] public partial bool IsPolygonArmed { get; private set; }

    /// <summary>The circle tool waits for a centre.</summary>
    [ObservableProperty] public partial bool IsCircleArmed { get; private set; }

    /// <summary>Takes the map tool now armed.</summary>
    public void SetTool(MapTool tool)
    {
        IsPolygonArmed = tool == MapTool.DrawPolygon;
        IsCircleArmed = tool == MapTool.DrawCircle;
    }

    /// <summary>Takes the plan's fences and the selection.</summary>
    public void Apply(IReadOnlyList<Fence> fences, string? selected)
    {
        foreach (var gone in _rows.Keys.Except(fences.Select(f => f.Id)).ToList())
        {
            _rows.Remove(gone);
        }

        Rows.Clear();
        foreach (var fence in fences)
        {
            if (!_rows.TryGetValue(fence.Id, out var row) || row.IsCircle != fence.Area is CircleArea)
            {
                _rows[fence.Id] = row = new FenceRowViewModel(fence, _plans, _select, _ui, _time);
            }

            row.Apply(fence);
            row.IsSelected = fence.Id == selected;
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void DrawPolygon() => _toggleTool(MapTool.DrawPolygon);

    [RelayCommand]
    private void DrawCircle() => _toggleTool(MapTool.DrawCircle);

    [RelayCommand]
    private void ClearFences() => _plans.ClearFences();

    /// <summary>A fence drawn on the map: a keep-out zone with a new id.</summary>
    internal void Add(Area area) => _plans.UpsertFence(new Fence(_plans.NextFenceId(), area));
}
