using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Controls.Map;
using Ketras.Environment.Weather;
using Ketras.Model.Environment;
using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Model.Planning;
using Ketras.Model.Vehicles;
using Ketras.Planning.Fences;
using Ketras.Planning.Missions;
using Ketras.Resources;
using Ketras.Station.Environment;
using Ketras.Station.Missions;
using Ketras.Station.Notifications;
using Ketras.Station.Settings;
using Ketras.Station.Terrain;
using Ketras.Station.Vehicles;
using Ketras.Telemetry;
using Ketras.Utilities;
using Microsoft.Extensions.Options;

namespace Ketras.Planning;

/// <summary>
/// The Planning activity: the map with the plan, the planning panel (mission or geofence editor), the view column
/// (base map, satellite or street, layers, follow, fit), and the telemetry and weather panels over
/// the map. The map's edit intents are applied to <see cref="PlanStore"/> here; selection is shared by the map
/// and the lists. Plans open from and save to <c>.plan</c> files.
/// </summary>
public sealed partial class PlanningViewModel : ObservableObject, IDisposable
{
    /// <summary>The planner's settings: base map.</summary>
    public static SettingDefinition<string> BaseMapSetting { get; } = new("map.base", nameof(BaseMapKind.Satellite));

    private static readonly FileKind PlanFile = new(Strings.PlanFileKind, "plan");

    private readonly PlanStore _plans;
    private readonly PlanValidationService _validation;
    private readonly EnvironmentStore _environment;
    private readonly VehicleRegistry _vehicles;
    private readonly MissionProgressTracker _progress;
    private readonly TerrainService _terrain;
    private readonly NotificationStore _notifications;
    private readonly SettingsStore _settings;
    private readonly IFileDialogService _files;
    private readonly UnitService _units;
    private readonly IUiDispatcher _ui;
    private Vehicle? _vehicle;
    private MissionItemType _pendingType = MissionItemType.Waypoint;

    /// <summary>Follows the plan, its findings, the environment, the vehicle and its mission progress.</summary>
    public PlanningViewModel(TelemetryPanelViewModel telemetry, WeatherViewModel weather, PlanStore plans, PlanValidationService validation,
        EnvironmentStore environment, VehicleRegistry vehicles, MissionProgressTracker progress,
        NotificationStore notifications, SettingsStore settings, StationPaths paths, IFileDialogService files, UnitService units, Setup.SetupContext setup,
        TerrainService terrain, IOptions<StationOptions> options, IUiDispatcher ui, TimeProvider time)
    {
        _terrain = terrain;
        Telemetry = telemetry;
        Weather = weather;
        _plans = plans;
        _validation = validation;
        _environment = environment;
        _vehicles = vehicles;
        _progress = progress;
        _notifications = notifications;
        _settings = settings;
        _files = files;
        _units = units;
        _ui = ui;
        TileCacheDirectory = paths.DataPath(null, "tiles");
        TerrainAttribution = options.Value.Terrain.Enabled ? Strings.TerrainCredit : string.Empty;
        ReadCustomTiles();
        settings.Changed += OnSetting;
        _terrain.CoverageChanged += OnCoverage;
        _terrain.Downloaded += OnTerrainDownloaded;
        ReadCoverage();
        BaseMap = Enum.TryParse<BaseMapKind>(settings.Get(BaseMapSetting), out var kind) ? kind : BaseMapKind.Satellite;
        Plan = plans.Current;
        Mission = new MissionPanelViewModel(plans, ui, time, ArmPlacement, id => SelectItem(id),
            (title, detail) => notifications.Notify(PlanStore.Source, NotificationSeverity.Warning, title, detail), terrain.GroundAt);
        Fences = new FencePanelViewModel(plans, setup, ui, time, tool => Tool = Tool == tool ? MapTool.Select : tool, id => SelectFence(id));
        Mission.Apply(Plan, null, null);
        Fences.Apply(Plan.Fences, null);

        _plans.Changed += OnPlanChanged;
        _validation.Changed += OnFindings;
        Telemetry.PropertyChanged += OnTelemetryChanged;
        _environment.Changed += OnEnvironment;
        _vehicles.ActiveChanged += OnActiveChanged;
        _progress.Changed += OnProgress;
        ReadEnvironment();
        OnFindings(validation.Findings);
        Follow(vehicles.Active);
    }

    /// <summary>Raised on the UI thread with each aircraft sample for the map.</summary>
    public event Action<MapAircraft>? AircraftUpdated;

    /// <summary>Asks the view to frame the plan.</summary>
    public event Action? FitRequested;

    /// <summary>The telemetry panel.</summary>
    public TelemetryPanelViewModel Telemetry { get; }

    /// <summary>The planning panel shows its editor; collapsed, only the Mission / Geofence switch.</summary>
    [ObservableProperty] public partial bool IsPanelExpanded { get; set; } = true;

    /// <summary>The weather panel.</summary>
    public WeatherViewModel Weather { get; }

    /// <summary>The mission editor.</summary>
    public MissionPanelViewModel Mission { get; }

    /// <summary>The geofence editor.</summary>
    public FencePanelViewModel Fences { get; }

    /// <summary>Where map tiles are cached.</summary>
    public string TileCacheDirectory { get; }

    /// <summary>The plan on the map.</summary>
    [ObservableProperty] public partial Plan Plan { get; private set; }

    /// <summary>Zones from the environment.</summary>
    [ObservableProperty] public partial IReadOnlyList<Zone> Zones { get; private set; } = [];

    /// <summary>Points of interest from the environment.</summary>
    [ObservableProperty] public partial IReadOnlyList<PointOfInterest> Points { get; private set; } = [];

    /// <summary>Where the plan meets the terrain.</summary>
    [ObservableProperty] public partial IReadOnlyList<GeoPoint> TerrainConflicts { get; private set; } = [];

    /// <summary>The selected item, shared by the map and the list.</summary>
    [ObservableProperty] public partial Guid? SelectedItemId { get; set; }

    /// <summary>The selected fence, shared by the map and the list.</summary>
    [ObservableProperty] public partial string? SelectedFenceId { get; set; }

    /// <summary>The plan index of the item the vehicle flies.</summary>
    [ObservableProperty] public partial int? ExecutingItemIndex { get; private set; }

    /// <summary>What a map click does.</summary>
    [ObservableProperty] public partial MapTool Tool { get; set; }

    /// <summary>The panel shows the geofence editor rather than the mission editor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMissionPanel))]
    public partial bool IsGeofencePanel { get; set; }

    /// <summary>The panel shows the mission editor.</summary>
    public bool IsMissionPanel => !IsGeofencePanel;

    /// <summary>Satellite or street.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSatellite))]
    public partial BaseMapKind BaseMap { get; private set; }

    /// <summary>The base map is satellite imagery.</summary>
    public bool IsSatellite => BaseMap == BaseMapKind.Satellite;

    /// <summary>The operator's own tile source from Settings; it replaces both built-in base maps.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitchBaseMap))]
    public partial CustomTileSource? CustomTiles { get; private set; }

    /// <summary>Satellite / street applies (no custom source).</summary>
    public bool CanSwitchBaseMap => CustomTiles is null;

    /// <summary>Tiles are drawn (else a grid).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Layers))] public partial bool ShowBaseMap { get; set; } = true;

    /// <summary>The mission is drawn.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Layers))] public partial bool ShowMission { get; set; } = true;

    /// <summary>Fences and zones are drawn.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Layers))] public partial bool ShowFences { get; set; } = true;

    /// <summary>The vehicle is drawn.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Layers))] public partial bool ShowVehicle { get; set; } = true;

    /// <summary>Traffic is drawn.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Layers))] public partial bool ShowTraffic { get; set; } = true;

    /// <summary>The selected item's guide drawings are on the map.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Layers))] public partial bool ShowGuides { get; set; } = true;

    /// <summary>
    /// The selected item's guide drawings: the vehicle's limits around it (tightest turns, smallest orbit, shortest
    /// approach or glide, where its altitude is reached) and its drag handles, labelled.
    /// </summary>
    [ObservableProperty] public partial IReadOnlyList<MapGuide> Guides { get; private set; } = [];

    /// <summary>The map keeps the vehicle centred.</summary>
    [ObservableProperty] public partial bool FollowVehicle { get; set; }

    /// <summary>The layers for the map.</summary>
    public MapLayers Layers => new() { BaseMap = ShowBaseMap, Mission = ShowMission, Fences = ShowFences, Ownship = ShowVehicle, Traffic = ShowTraffic, Guides = ShowGuides };

    /// <summary>The terrain data's credit while terrain is checked; empty without. Shown with the base map's credit behind the view column's info mark.</summary>
    [ObservableProperty] public partial string TerrainAttribution { get; private set; } = string.Empty;

    /// <summary>Terrain is downloaded by hand and cells of the plan's area are missing: the download button shows.</summary>
    [ObservableProperty] public partial bool ShowTerrainDownload { get; private set; }

    /// <summary>Terrain cells are being downloaded.</summary>
    [ObservableProperty] public partial bool IsTerrainDownloading { get; private set; }

    /// <summary>The download button's tooltip: the cells it fetches, or that it is busy.</summary>
    [ObservableProperty] public partial string TerrainDownloadTip { get; private set; } = string.Empty;

    /// <inheritdoc />
    public void Dispose()
    {
        _terrain.CoverageChanged -= OnCoverage;
        _terrain.Downloaded -= OnTerrainDownloaded;
        _plans.Changed -= OnPlanChanged;
        _validation.Changed -= OnFindings;
        Telemetry.PropertyChanged -= OnTelemetryChanged;
        _environment.Changed -= OnEnvironment;
        _vehicles.ActiveChanged -= OnActiveChanged;
        _progress.Changed -= OnProgress;
        _settings.Changed -= OnSetting;
        Follow(null);
    }

    /// <summary>Selects an item (and no fence).</summary>
    public void SelectItem(Guid? id)
    {
        SelectedItemId = id;
        if (id is not null)
        {
            SelectedFenceId = null;
        }
    }

    /// <summary>Selects a fence (and no item).</summary>
    public void SelectFence(string? id)
    {
        SelectedFenceId = id;
        if (id is not null)
        {
            SelectedItemId = null;
        }
    }

    [RelayCommand]
    private void TogglePanel() => IsPanelExpanded = !IsPanelExpanded;

    [RelayCommand]
    private void ShowMissionPanel()
    {
        Tool = MapTool.Select;
        IsGeofencePanel = false;
    }

    [RelayCommand]
    private void ShowGeofencePanel()
    {
        Tool = MapTool.Select;
        IsGeofencePanel = true;
    }

    [RelayCommand]
    private void SwitchBaseMap()
    {
        BaseMap = BaseMap == BaseMapKind.Satellite ? BaseMapKind.Street : BaseMapKind.Satellite;
        _settings.Set(BaseMapSetting, BaseMap.ToString());
    }

    [RelayCommand]
    private void ToggleBaseMapLayer() => ShowBaseMap = !ShowBaseMap;

    [RelayCommand]
    private void ToggleMissionLayer() => ShowMission = !ShowMission;

    [RelayCommand]
    private void ToggleFenceLayer() => ShowFences = !ShowFences;

    [RelayCommand]
    private void ToggleVehicleLayer() => ShowVehicle = !ShowVehicle;

    [RelayCommand]
    private void ToggleTrafficLayer() => ShowTraffic = !ShowTraffic;

    [RelayCommand]
    private void ToggleGuidesLayer() => ShowGuides = !ShowGuides;

    [RelayCommand]
    private void ToggleFollow() => FollowVehicle = !FollowVehicle;

    [RelayCommand]
    private void Fit() => FitRequested?.Invoke();

    [RelayCommand]
    private void PlaceItem(GeoPoint point)
    {
        if (_plans.AddItem(_pendingType, point) is { } id)
        {
            SelectItem(id);
        }
    }

    [RelayCommand]
    private void MoveItem(ItemMove move) => _plans.UpdateItem(move.ItemId, item => item with { Location = move.Location });

    [RelayCommand]
    private void ResizeOrbit(OrbitResize resize) =>
        _plans.UpdateItem(resize.ItemId, item => MissionItemValues.LoiterRadiusMeters(item) is null ? item
            : item.Type == MissionItemType.LoiterToAltitude ? MissionItemParameters.Write(item, MissionItemField.OrbitRadius, resize.RadiusMeters)
            : item with { Param3 = item.Param3 < 0 ? -resize.RadiusMeters : resize.RadiusMeters });

    [RelayCommand]
    private void MoveApproach(ApproachMove move) =>
        _plans.UpdateItem(move.ItemId, item => item with { Param3 = move.LengthMeters, Param4 = move.HeadingDegrees });

    [RelayCommand]
    private void AddFence(Area area)
    {
        Fences.Add(area);
        IsGeofencePanel = true;
    }

    [RelayCommand]
    private void EditFence(FenceEdit edit)
    {
        if (_plans.Current.Fences.FirstOrDefault(f => f.Id == edit.FenceId) is { } fence)
        {
            _plans.UpsertFence(fence with { Area = edit.Area });
        }
    }

    [RelayCommand]
    private void DrawingDiscarded() =>
        _notifications.Notify(PlanStore.Source, NotificationSeverity.Info, Strings.PlanningPolygonDiscarded, Strings.PlanningPolygonNeedsCorners);

    [RelayCommand]
    private async Task OpenPlanAsync()
    {
        if (await _files.OpenAsync(Strings.PlanningOpenPlan, PlanFile) is not { } path)
        {
            return;
        }

        try
        {
            var result = PlanFileFormat.Read(await File.ReadAllTextAsync(path));
            _plans.Apply(_ => result.Plan, PlanChange.Mission | PlanChange.Fences);
            var detail = result.Skipped.Count == 0 ? Path.GetFileName(path)
                : string.Format(CultureInfo.CurrentCulture, Strings.PlanSkipped, result.Skipped.Count, string.Join("; ", result.Skipped));
            _notifications.Notify(PlanStore.Source, result.Skipped.Count == 0 ? NotificationSeverity.Success : NotificationSeverity.Warning, Strings.PlanOpened, detail);
            FitRequested?.Invoke();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            _notifications.Notify(PlanStore.Source, NotificationSeverity.Error, Strings.PlanNotOpened, exception.Message);
        }
    }

    [RelayCommand]
    private async Task SavePlanAsync()
    {
        if (await _files.SaveAsync(Strings.PlanningSavePlan, PlanFile, "mission.plan") is not { } path)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, PlanFileFormat.Write(_plans.Current, _terrain.GroundAt));
            _notifications.Notify(PlanStore.Source, NotificationSeverity.Success, Strings.PlanSaved, Path.GetFileName(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _notifications.Notify(PlanStore.Source, NotificationSeverity.Error, Strings.PlanNotSaved, exception.Message);
        }
    }

    partial void OnSelectedItemIdChanged(Guid? value)
    {
        Mission.Apply(Plan, value, ExecutingItemIndex);
        UpdateGuides();
    }

    /// <summary>The selected item's guides for the plan, the active vehicle's limits and the terrain.</summary>
    private void UpdateGuides() =>
        Guides = [.. MissionGuideRules.For(Plan.Mission, SelectedItemId, _plans.Limits, _terrain.GroundAt).Select(guide => new MapGuide(
            guide.Shape switch { GuideShape.Ring => MapGuideShape.Ring, GuideShape.Handle => MapGuideShape.Handle, _ => MapGuideShape.Mark },
            guide.Position, guide.RadiusMeters, MissionLabels.Guide(guide), guide.Warning))];

    partial void OnSelectedFenceIdChanged(string? value) => Fences.Apply(Plan.Fences, value);

    partial void OnToolChanged(MapTool value)
    {
        Mission.SetArmed(value == MapTool.AddItem ? MissionItemCatalog.PhaseOf(_pendingType) : null);
        Fences.SetTool(value);
    }

    private void OnSetting(string key)
    {
        if (key == Settings.AppSettings.CustomTileUrl.Key || key == Settings.AppSettings.CustomTileAttribution.Key)
        {
            _ui.Post(ReadCustomTiles);
        }
        else if (key == TerrainService.DownloadModeSetting.Key)
        {
            _ui.Post(ReadCoverage);
        }
    }

    private void OnCoverage() => _ui.Post(ReadCoverage);

    private void ReadCoverage()
    {
        var missing = _terrain.MissingCells;
        IsTerrainDownloading = _terrain.IsDownloading;
        ShowTerrainDownload = _terrain.IsEnabled && _terrain.DownloadMode == TerrainDownloadMode.Manual && (missing.Count > 0 || IsTerrainDownloading);
        TerrainDownloadTip = IsTerrainDownloading
            ? Strings.PlanningDownloadingTerrain
            : string.Format(CultureInfo.CurrentCulture, Strings.PlanningDownloadTerrain, string.Join(", ", missing));
    }

    [RelayCommand]
    private async Task DownloadTerrainAsync()
    {
        try
        {
            await _terrain.DownloadPlanAreaAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException)
        {
            _notifications.Notify(TerrainSource, NotificationSeverity.Error, Strings.TerrainDownloadIncomplete, exception.Message);
        }
    }

    private const string TerrainSource = "Terrain";

    /// <summary>Says what a download of the plan's area got, in either mode.</summary>
    private void OnTerrainDownloaded(Ketras.Terrain.TileFetchResult result)
    {
        var problems = result.Failed.Select(f => $"{f.Cell}: {f.Reason}")
            .Concat(result.NotPublished.Select(cell => string.Format(CultureInfo.CurrentCulture, Strings.TerrainNotPublished, cell)))
            .ToList();
        if (problems.Count > 0)
        {
            _notifications.Notify(TerrainSource, NotificationSeverity.Warning, Strings.TerrainDownloadIncomplete, string.Join("; ", problems));
        }
        else if (result.Downloaded.Count > 0)
        {
            _notifications.Notify(TerrainSource, NotificationSeverity.Success, Strings.TerrainDownloaded, string.Join(", ", result.Downloaded));
        }
    }

    private void ReadCustomTiles()
    {
        var url = _settings.Get(Settings.AppSettings.CustomTileUrl);
        CustomTiles = url.Length > 0 ? new CustomTileSource(url, _settings.Get(Settings.AppSettings.CustomTileAttribution)) : null;
    }

    private void ArmPlacement(MissionItemType type)
    {
        _pendingType = type;
        Tool = MapTool.AddItem;
    }

    private void OnPlanChanged(Plan plan, PlanChange change) => _ui.Post(() =>
    {
        Plan = plan;
        if (SelectedItemId is { } item && plan.Mission.Items.All(i => i.Id != item))
        {
            SelectedItemId = null;
        }

        if (SelectedFenceId is { } fence && plan.Fences.All(f => f.Id != fence))
        {
            SelectedFenceId = null;
        }

        Mission.Apply(plan, SelectedItemId, ExecutingItemIndex);
        Fences.Apply(plan.Fences, SelectedFenceId);
        UpdateGuides();
    });

    private void OnFindings(IReadOnlyList<PlanFinding> findings) => _ui.Post(() =>
    {
        TerrainConflicts = [.. findings.Where(f => f.Source == FindingSource.Terrain && f.Location is not null).Select(f => f.Location!.Value)];
        Mission.ApplyEstimate(_validation.Estimate, _validation.Budget, _units.Distance);
        Mission.RefreshLimits();
        UpdateGuides();
    });

    private void OnTelemetryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TelemetryPanelViewModel.Waypoint) or nameof(TelemetryPanelViewModel.WaypointEta))
        {
            Mission.ShowProgress(Telemetry.Waypoint == TelemetryPanelViewModel.None ? string.Empty : $"{Telemetry.Waypoint} · {Telemetry.WaypointEta}");
        }
    }

    private void OnEnvironment(EnvironmentChange change)
    {
        if ((change & (EnvironmentChange.Traffic | EnvironmentChange.Zones | EnvironmentChange.Points)) != 0)
        {
            _ui.Post(ReadEnvironment);
        }
    }

    private void ReadEnvironment()
    {
        Zones = _environment.Zones;
        Points = _environment.Points;
        foreach (var target in _environment.Traffic)
        {
            AircraftUpdated?.Invoke(new MapAircraft(target.Id, target.Callsign ?? target.Id, false, target.Position, target.AltitudeMeters ?? 0,
                target.HeadingDegrees ?? 0, target.GroundSpeedMps ?? 0, target.Category.ToString(), target.SampleAge));
        }
    }

    private void OnProgress(MissionProgress progress) => _ui.Post(() =>
    {
        ExecutingItemIndex = progress.State == MissionExecution.Executing ? progress.CurrentItemIndex : null;
        Mission.Apply(Plan, SelectedItemId, ExecutingItemIndex);
    });

    private void OnActiveChanged(Vehicle? vehicle) => _ui.Post(() => Follow(vehicle));

    private void Follow(Vehicle? vehicle)
    {
        if (_vehicle is not null)
        {
            _vehicle.SnapshotChanged -= OnSnapshot;
        }

        _vehicle = vehicle;
        if (vehicle is not null)
        {
            vehicle.SnapshotChanged += OnSnapshot;
        }
    }

    private void OnSnapshot(VehicleSnapshot snapshot)
    {
        if (snapshot.Position is { } position)
        {
            _ui.Post(() => AircraftUpdated?.Invoke(new MapAircraft(snapshot.Id.ToString(), snapshot.Name, true, position,
                snapshot.AltitudeAmslMeters ?? 0, snapshot.HeadingDegrees ?? 0, snapshot.GroundSpeedMps ?? 0, snapshot.Mode)));
        }
    }
}
