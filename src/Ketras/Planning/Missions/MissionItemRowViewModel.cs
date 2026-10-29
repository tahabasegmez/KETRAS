using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Model.Vehicles;
using Ketras.Resources;
using Ketras.Utilities;

namespace Ketras.Planning.Missions;

/// <summary>A jump target choice: an item by its number.</summary>
/// <param name="ItemId">The item.</param>
/// <param name="Label">"3 · Waypoint".</param>
public sealed record JumpTargetChoice(Guid ItemId, string Label);

/// <summary>
/// One mission item as a list row (docs/UI_RULES.md "List row", "Row details"): its number, icon, name and summary,
/// and while selected its fields underneath, each with the span the vehicle and the terrain allow. The row is kept across edits and updated in place, so a field keeps its
/// flash and the list does not jump.
/// </summary>
public sealed partial class MissionItemRowViewModel : ObservableObject
{
    private readonly PlanStore _plans;
    private readonly Action<Guid?> _select;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private bool _applying;

    /// <summary>Builds the row of <paramref name="item"/>.</summary>
    public MissionItemRowViewModel(MissionItem item, PlanStore plans, Action<Guid?> select, IUiDispatcher ui, TimeProvider time)
    {
        _plans = plans;
        _select = select;
        _ui = ui;
        _time = time;
        Item = item;
        Fields = [.. MissionItemParameters.For(item.Type).Select(CreateField)];
        if (item.UsesLocation)
        {
            Latitude = Coordinate("latitude", Strings.PlanningLatitude, item.Location?.Latitude ?? double.NaN, -90, 90,
                value => Update(i => i with { Location = new GeoPoint(value, i.Location?.Longitude ?? 0) }));
            Longitude = Coordinate("longitude", Strings.PlanningLongitude, item.Location?.Longitude ?? double.NaN, -180, 180,
                value => Update(i => i with { Location = new GeoPoint(i.Location?.Latitude ?? 0, value) }));
        }
    }

    /// <summary>The item as the plan has it now.</summary>
    public MissionItem Item { get; private set; }

    /// <summary>Its id.</summary>
    public Guid Id => Item.Id;

    /// <summary>Its type's name.</summary>
    public string Name => MissionLabels.Of(Item.Type);

    /// <summary>What the item does, shown at the top of its fields.</summary>
    public string Help => MissionLabels.Help(Item.Type);

    /// <summary>Its type's icon.</summary>
    public string Icon => MissionLabels.Icon(Item.Type);

    /// <summary>Its phase, for the colour bar.</summary>
    public MissionPhase Phase => Item.Phase;

    /// <summary>Its number in the list, from 1.</summary>
    [ObservableProperty] public partial int Number { get; private set; }

    /// <summary>A short value, e.g. "120 m".</summary>
    [ObservableProperty] public partial string Summary { get; private set; } = string.Empty;

    /// <summary>It is selected (its fields show).</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>It can move earlier within its phase.</summary>
    [ObservableProperty] public partial bool CanMoveUp { get; private set; }

    /// <summary>It can move later within its phase.</summary>
    [ObservableProperty] public partial bool CanMoveDown { get; private set; }

    /// <summary>The vehicle is flying it now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value))]
    public partial bool IsExecuting { get; set; }

    /// <summary>While flown: the distance and time to it, e.g. "640 m · 1:20"; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value))]
    public partial string Progress { get; set; } = string.Empty;

    /// <summary>The row's value: the progress while flown, else the summary.</summary>
    public string Value => IsExecuting && Progress.Length > 0 ? Progress : Summary;

    partial void OnSummaryChanged(string value) => OnPropertyChanged(nameof(Value));

    /// <summary>Its editable values.</summary>
    public ObservableCollection<FieldViewModel> Fields { get; }

    /// <summary>Latitude field; null for items without a position.</summary>
    public FieldViewModel? Latitude { get; }

    /// <summary>Longitude field; null for items without a position.</summary>
    public FieldViewModel? Longitude { get; }

    /// <summary>It has a position.</summary>
    public bool HasLocation => Latitude is not null;

    /// <summary>It is a jump.</summary>
    public bool IsJump => Item.Type == MissionItemType.DoJump;

    /// <summary>Items a jump can target.</summary>
    public ObservableCollection<JumpTargetChoice> JumpTargets { get; } = [];

    /// <summary>The jump's target.</summary>
    [ObservableProperty] public partial JumpTargetChoice? JumpTarget { get; set; }

    /// <summary>Takes the plan's current state of the item, and its fields' spans for <paramref name="limits"/>.</summary>
    public void Apply(MissionItem item, int index, Mission mission, VehicleLimits? limits = null, Func<GeoPoint, double?>? groundAt = null)
    {
        _applying = true;
        Item = item;
        Number = index + 1;
        var phaseItems = mission.Items.Where(i => i.Phase == item.Phase).ToList();
        CanMoveUp = phaseItems.FirstOrDefault()?.Id != item.Id;
        CanMoveDown = phaseItems.LastOrDefault()?.Id != item.Id;
        foreach (var field in Fields)
        {
            var parameter = MissionItemParameters.For(item.Type).First(p => p.Key == field.Key);
            if (parameter.Quantity != Quantity.Choice)
            {
                var range = MissionParameterRules.Range(mission, item, parameter, limits ?? VehicleLimits.Default, groundAt);
                var (tip, line) = MissionLabels.Range(range, parameter.Quantity);
                field.SetRange(range, tip, line);
            }

            field.Apply(MissionItemParameters.Read(item, parameter.Field));
        }

        Latitude?.Apply(item.Location?.Latitude ?? double.NaN);
        Longitude?.Apply(item.Location?.Longitude ?? double.NaN);
        int? targetNumber = null;
        if (IsJump)
        {
            JumpTargets.Clear();
            for (var i = 0; i < mission.Items.Count; i++)
            {
                if (mission.Items[i].Id != item.Id && mission.Items[i].UsesLocation)
                {
                    JumpTargets.Add(new JumpTargetChoice(mission.Items[i].Id, string.Create(CultureInfo.CurrentCulture, $"{i + 1} · {MissionLabels.Of(mission.Items[i].Type)}")));
                }

                if (mission.Items[i].Id == item.JumpTargetId)
                {
                    targetNumber = i + 1;
                }
            }

            JumpTarget = JumpTargets.FirstOrDefault(t => t.ItemId == item.JumpTargetId);
        }

        Summary = MissionLabels.Summary(item, targetNumber, mission.Settings.AltitudeReference);
        OnPropertyChanged(nameof(Name));
        _applying = false;
    }

    /// <summary>Selects the row, or clears the selection when it is selected already.</summary>
    [RelayCommand]
    private void Select() => _select(IsSelected ? null : Id);

    [RelayCommand]
    private void MoveUp() => _plans.MoveItem(Id, -1);

    [RelayCommand]
    private void MoveDown() => _plans.MoveItem(Id, 1);

    [RelayCommand]
    private void Delete() => _plans.RemoveItem(Id);

    partial void OnJumpTargetChanged(JumpTargetChoice? value)
    {
        if (!_applying && value is not null && value.ItemId != Item.JumpTargetId)
        {
            Update(i => i with { JumpTargetId = value.ItemId });
        }
    }

    private bool Update(Func<MissionItem, MissionItem> change) => _plans.UpdateItem(Id, change);

    private FieldViewModel CreateField(MissionParameter parameter)
    {
        var value = MissionItemParameters.Read(Item, parameter.Field);
        bool Store(double v) => Update(i => MissionItemParameters.Write(i, parameter.Field, v));
        return parameter.Choices is { Count: > 0 } choices
            ? new FieldViewModel(parameter.Key, MissionLabels.Parameter(parameter.Key), [.. choices.Select(c => new FieldChoice(c.Value, MissionLabels.Choice(c.Key)))], value, Store, _ui, _time)
            : new FieldViewModel(parameter.Key, MissionLabels.Parameter(parameter.Key), MissionLabels.Unit(parameter.Quantity), value,
                MissionLabels.Decimals(parameter.Quantity), parameter.Minimum, parameter.Maximum, Store, _ui, _time);
    }

    private FieldViewModel Coordinate(string key, string label, double value, double minimum, double maximum, Func<double, bool> store) =>
        new(key, label, "°", value, 6, minimum, maximum, store, _ui, _time);
}
