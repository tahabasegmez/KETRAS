using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Model.Planning;
using Ketras.Model.Vehicles;
using Ketras.Resources;
using Ketras.Utilities;

namespace Ketras.Planning.Missions;

/// <summary>An item type offered by the picker.</summary>
/// <param name="Type">The type.</param>
/// <param name="Name">Its name.</param>
/// <param name="Icon">Its Lucide icon.</param>
/// <param name="Phase">Its phase, for the icon colour.</param>
public sealed record ItemTypeChoice(MissionItemType Type, string Name, string Icon, MissionPhase Phase)
{
    /// <summary>"Waypoint" and what it does, for the tile's tooltip.</summary>
    public string Tip => $"{Name}\n{MissionLabels.Help(Type)}";
}

/// <summary>The picker's button of one phase: it opens that phase's item types; it is armed while one of them waits for a map click.</summary>
public sealed partial class PhaseGroup(MissionPhase phase, string title, string icon, IReadOnlyList<ItemTypeChoice> types) : ObservableObject
{
    /// <summary>The phase.</summary>
    public MissionPhase Phase { get; } = phase;

    /// <summary>Its title.</summary>
    public string Title { get; } = title;

    /// <summary>Its icon.</summary>
    public string Icon { get; } = icon;

    /// <summary>Its item types.</summary>
    public IReadOnlyList<ItemTypeChoice> Types { get; } = types;

    /// <summary>One of its types waits for a map click.</summary>
    [ObservableProperty] public partial bool IsArmed { get; set; }
}

/// <summary>
/// The mission editor of the planning panel: the mission settings, the items in flying order (one row each, kept by
/// id) and the picker that adds an item. A positioned type arms the map's place tool; other types are added at
/// once. Every edit goes through <see cref="PlanStore"/>, which reports refusals.
/// </summary>
public sealed partial class MissionPanelViewModel : ObservableObject
{
    private readonly PlanStore _plans;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly Action<MissionItemType> _armPlacement;
    private readonly Action<Guid?> _select;
    private readonly Action<string, string> _refused;
    private readonly Func<GeoPoint, double?>? _groundAt;
    private readonly Dictionary<Guid, MissionItemRowViewModel> _rows = [];
    private (Plan Plan, Guid? Selected, int? Executing)? _applied;
    private Model.Vehicles.VehicleLimits? _appliedLimits;

    /// <summary>Edits the plan in <paramref name="plans"/>.</summary>
    public MissionPanelViewModel(PlanStore plans, IUiDispatcher ui, TimeProvider time, Action<MissionItemType> armPlacement, Action<Guid?> select,
        Action<string, string> refused, Func<GeoPoint, double?>? groundAt = null)
    {
        _refused = refused;
        _groundAt = groundAt;
        _plans = plans;
        _ui = ui;
        _time = time;
        _armPlacement = armPlacement;
        _select = select;
        var settings = plans.Current.Mission.Settings;
        CruiseSpeed = Number("cruise-speed", Strings.PlanningCruiseSpeed, "m/s", settings.CruiseSpeedMps, 1, 1, 120, v => Settings(s => s with { CruiseSpeedMps = v }));
        DefaultAltitude = Number("default-altitude", Strings.PlanningDefaultAltitude, "m", settings.DefaultAltitudeMeters, 0, 1, 20000, v => Settings(s => s with { DefaultAltitudeMeters = v }));
        EndAction = new FieldViewModel("end-action", Strings.PlanningEndAction,
            [.. Enum.GetValues<MissionEndAction>().Select(a => new FieldChoice((int)a, MissionLabels.Of(a)))], (int)settings.EndAction,
            v => Settings(s => s with { EndAction = (MissionEndAction)(int)v }), ui, time) { Help = Strings.PlanningEndActionHelp };
        AltitudeReference = new FieldViewModel("altitude-reference", Strings.PlanningAltitudeReference,
            [
                new FieldChoice((int)Model.Missions.AltitudeReference.RelativeToHome, Strings.PlanningReferenceRelative),
                new FieldChoice((int)Model.Missions.AltitudeReference.AboveSeaLevel, Strings.PlanningReferenceAmsl),
                new FieldChoice((int)Model.Missions.AltitudeReference.AboveTerrain, Strings.PlanningReferenceTerrain)
            ], (int)settings.AltitudeReference, v => SetReference((AltitudeReference)(int)v), ui, time) { Help = Strings.PlanningAltitudeReferenceHelp };
        LandingTerrain = new FieldViewModel("landing-terrain", Strings.PlanningRangefinderUse,
            [
                new FieldChoice((int)LandingTerrainUse.Off, Strings.PlanningRangefinderOff),
                new FieldChoice((int)LandingTerrainUse.Flare, Strings.PlanningRangefinderFlare),
                new FieldChoice((int)LandingTerrainUse.GlideSlope, Strings.PlanningRangefinderGlideSlope)
            ], (int)settings.Landing.TerrainUse, v => Settings(s => s with { Landing = s.Landing with { TerrainUse = (LandingTerrainUse)(int)v } }), ui, time)
        { Help = Strings.PlanningRangefinderUseHelp };
        AbortIfNotFound = YesNo("abort-not-found", Strings.PlanningAbortNotFound, settings.Landing.AbortIfTerrainNotFound,
            v => Settings(s => s with { Landing = s.Landing with { AbortIfTerrainNotFound = v } }), Strings.PlanningAbortNotFoundHelp);
        AbortIfLost = YesNo("abort-lost", Strings.PlanningAbortLost, settings.Landing.AbortIfTerrainLost,
            v => Settings(s => s with { Landing = s.Landing with { AbortIfTerrainLost = v } }), Strings.PlanningAbortLostHelp);
        Groups = [.. new[] { MissionPhase.Takeoff, MissionPhase.Cruise, MissionPhase.Landing }.Select(phase => new PhaseGroup(phase, MissionLabels.Button(phase), MissionLabels.Icon(phase),
            [.. MissionItemCatalog.TypesInPhase(phase).Select(type => new ItemTypeChoice(type, MissionLabels.Of(type), MissionLabels.Icon(type), phase))]))];
        Apply(plans.Current, null, null);
    }

    /// <summary>The flight estimate, e.g. "≈ 14:20 · 18.2 km · margin 32 %"; empty without items.</summary>
    [ObservableProperty] public partial string Estimate { get; private set; } = string.Empty;

    /// <summary>How the estimate is coloured: by the energy margin.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEstimateWarning), nameof(IsEstimateError))]
    public partial CheckLevel EstimateLevel { get; private set; }

    /// <summary>The margin is below <see cref="EnergyCalculator.WarningMarginPercent"/>.</summary>
    public bool IsEstimateWarning => EstimateLevel == CheckLevel.Warning;

    /// <summary>The mission does not fit in the battery.</summary>
    public bool IsEstimateError => EstimateLevel == CheckLevel.Error;

    /// <summary>The estimate's tooltip: how it is made, and what is missing for the energy.</summary>
    [ObservableProperty] public partial string EstimateDetail { get; private set; } = Strings.PlanningEstimateHelp;

    /// <summary>Shows the flight estimate and its energy budget (null when unknown).</summary>
    public void ApplyEstimate(MissionEstimate estimate, EnergyBudget? budget, Func<double, string> distance)
    {
        if (estimate.Legs.Count == 0)
        {
            Estimate = string.Empty;
            EstimateLevel = CheckLevel.Unknown;
            return;
        }

        var time = estimate.IsOpenEnded ? Strings.PlanningEstimateOpen : Telemetry.TelemetryPanelViewModel.Duration(estimate.Seconds);
        Estimate = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.PlanningEstimate, time, distance(estimate.DistanceMeters))
                   + (budget is null ? string.Empty : string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.PlanningEstimateMargin, budget.MarginPercent));
        EstimateLevel = budget is null ? CheckLevel.Unknown
            : budget.MarginMah < 0 ? CheckLevel.Error
            : budget.MarginPercent < EnergyCalculator.WarningMarginPercent ? CheckLevel.Warning
            : CheckLevel.Pass;
        EstimateDetail = budget is null ? $"{Strings.PlanningEstimateHelp}\n\n{Strings.PlanningEstimateNoEnergy}" : Strings.PlanningEstimateHelp;
    }

    /// <summary>Shows the distance and time to the item being flown on its row.</summary>
    public void ShowProgress(string progress)
    {
        foreach (var row in Rows)
        {
            row.Progress = row.IsExecuting ? progress : string.Empty;
        }
    }

    /// <summary>Cruise speed.</summary>
    public FieldViewModel CruiseSpeed { get; }

    /// <summary>Altitude of new items.</summary>
    public FieldViewModel DefaultAltitude { get; }

    /// <summary>What the vehicle does at the end.</summary>
    public FieldViewModel EndAction { get; }

    /// <summary>What item altitudes are measured from; changing it converts the items.</summary>
    public FieldViewModel AltitudeReference { get; }

    /// <summary>How the landing uses the rangefinder (FW_LND_USETER).</summary>
    public FieldViewModel LandingTerrain { get; }

    /// <summary>Abort the landing when the rangefinder finds no ground (FW_LND_ABORT bit 0).</summary>
    public FieldViewModel AbortIfNotFound { get; }

    /// <summary>Abort the landing when the rangefinder loses the ground (FW_LND_ABORT bit 1).</summary>
    public FieldViewModel AbortIfLost { get; }

    /// <summary>The items in flying order.</summary>
    public ObservableCollection<MissionItemRowViewModel> Rows { get; } = [];

    /// <summary>The picker's groups.</summary>
    public IReadOnlyList<PhaseGroup> Groups { get; }

    /// <summary>There are no items.</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>
    /// The end action shows: the plan has no landing-phase item, so the encoder appends RTL or the vehicle holds. With a
    /// landing item it is ignored (MissionEncoder), and a note says so instead.
    /// </summary>
    [ObservableProperty] public partial bool ShowEndAction { get; private set; } = true;

    /// <summary>Takes the vehicle's limits again when they changed since the last <see cref="Apply"/>, for the fields' spans.</summary>
    public void RefreshLimits()
    {
        if (_applied is { } applied && _plans.Limits != _appliedLimits)
        {
            Apply(applied.Plan, applied.Selected, applied.Executing);
        }
    }

    /// <summary>Takes the plan, the selection and the item the vehicle flies.</summary>
    public void Apply(Plan plan, Guid? selected, int? executing)
    {
        _applied = (plan, selected, executing);
        var limits = _appliedLimits = _plans.Limits;
        var settings = plan.Mission.Settings;
        ShowEndAction = !plan.Mission.Items.Any(item => item.Phase == MissionPhase.Landing);
        CruiseSpeed.Apply(settings.CruiseSpeedMps);
        DefaultAltitude.Apply(settings.DefaultAltitudeMeters);
        EndAction.Apply((int)settings.EndAction);
        AltitudeReference.Apply((int)settings.AltitudeReference);
        LandingTerrain.Apply((int)settings.Landing.TerrainUse);
        AbortIfNotFound.Apply(settings.Landing.AbortIfTerrainNotFound ? 1 : 0);
        AbortIfLost.Apply(settings.Landing.AbortIfTerrainLost ? 1 : 0);

        var items = plan.Mission.Items;
        foreach (var gone in _rows.Keys.Except(items.Select(i => i.Id)).ToList())
        {
            _rows.Remove(gone);
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!_rows.TryGetValue(item.Id, out var row) || row.Item.Type != item.Type)
            {
                _rows[item.Id] = row = new MissionItemRowViewModel(item, _plans, _select, _ui, _time);
            }

            row.Apply(item, i, plan.Mission, limits, _groundAt);
            row.IsSelected = item.Id == selected;
            row.IsExecuting = i == executing;
            if (i < Rows.Count && ReferenceEquals(Rows[i], row))
            {
                continue;
            }

            var existing = Rows.IndexOf(row);
            if (existing >= 0)
            {
                Rows.Move(existing, i);
            }
            else
            {
                Rows.Insert(i, row);
            }
        }

        while (Rows.Count > items.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>The type can be added to the mission now (the picker dims the others).</summary>
    public bool CanAdd(MissionItemType type) => MissionRules.CanAdd(_plans.Current.Mission, type, out _);

    /// <summary><see cref="CanAdd"/> for the picker.</summary>
    public Func<ItemTypeChoice, bool> CanAddChoice => choice => CanAdd(choice.Type);

    /// <summary>Marks the phase whose item type waits for a map click; null for none.</summary>
    public void SetArmed(MissionPhase? phase)
    {
        foreach (var group in Groups)
        {
            group.IsArmed = group.Phase == phase;
        }
    }

    [RelayCommand]
    private void AddItem(ItemTypeChoice choice)
    {
        if (!MissionRules.CanAdd(_plans.Current.Mission, choice.Type, out var reason))
        {
            _refused(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.PlanningCannotAdd, choice.Name), reason ?? string.Empty);
            return;
        }

        if (MissionItemCatalog.UsesLocation(choice.Type))
        {
            _armPlacement(choice.Type);
        }
        else if (_plans.AddItem(choice.Type, null) is { } id)
        {
            _select(id);
        }
    }

    [RelayCommand]
    private void ClearMission() => _plans.ClearMission();

    private bool Settings(Func<MissionSettings, MissionSettings> change)
    {
        _plans.UpdateSettings(change);
        return true;
    }

    private FieldViewModel Number(string key, string label, string unit, double value, int decimals, double minimum, double maximum, Func<double, bool> store) =>
        new(key, label, unit, value, decimals, minimum, maximum, store, _ui, _time);

    private FieldViewModel YesNo(string key, string label, bool value, Func<bool, bool> store, string help) =>
        new(key, label, [new FieldChoice(0, Strings.PlanningNo), new FieldChoice(1, Strings.PlanningYes)], value ? 1 : 0, v => store(v >= 0.5), _ui, _time) { Help = help };

    /// <summary>Converts the plan to another altitude reference; refused, with the item named, when its terrain is not known.</summary>
    private bool SetReference(AltitudeReference reference)
    {
        if (_plans.SetAltitudeReference(reference, _groundAt) is not { } item)
        {
            return true;
        }

        _refused(Strings.PlanningReferenceRefused, string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.PlanningReferenceNoTerrain, item));
        return false;
    }
}
