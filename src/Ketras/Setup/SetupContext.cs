using CommunityToolkit.Mvvm.ComponentModel;
using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Protocols;
using Ketras.Parameters;
using Ketras.Utilities;
using Ketras.Vehicles;

namespace Ketras.Setup;

/// <summary>
/// The setup screens' shared view of the active vehicle's parameters: its values, the firmware metadata, the operator's
/// pending changes and whether editing is allowed. Every setup page and the planner's geofence settings read through it
/// and edit through <see cref="Set"/>, which stages a change in <see cref="PendingParameterStore"/>. Its events are
/// raised on the UI thread.
/// </summary>
public sealed partial class SetupContext : ObservableObject, IDisposable
{
    private readonly VehicleRegistry _vehicles;
    private readonly MetadataService _metadata;
    private readonly PendingParameterStore _pending;
    private readonly IUiDispatcher _ui;
    private Vehicle? _vehicle;

    /// <summary>Follows the active vehicle.</summary>
    public SetupContext(VehicleRegistry vehicles, MetadataService metadata, PendingParameterStore pending, IUiDispatcher ui)
    {
        _vehicles = vehicles;
        _metadata = metadata;
        _pending = pending;
        _ui = ui;
        Reader = name => Shown(name);
        vehicles.ActiveChanged += OnActiveChanged;
        metadata.Loaded += OnMetadataLoaded;
        pending.Changed += OnPendingChanged;
        Follow(vehicles.Active);
    }

    /// <summary>A parameter's value or pending value changed; null name means many did (a full read, a new vehicle, a discard).</summary>
    public event Action<string?>? ValuesChanged;

    /// <summary>The vehicle, its metadata or the edit state changed.</summary>
    public event Action? StateChanged;

    /// <summary>The vehicle's health and arming check report changed; raised on the UI thread.</summary>
    public event Action? HealthChanged;

    /// <summary>The active vehicle.</summary>
    public Vehicle? Vehicle => _vehicle;

    /// <summary>The metadata of the active vehicle; empty until loaded.</summary>
    public VehicleMetadata Metadata { get; private set; } = VehicleMetadata.Empty;

    /// <summary>The metadata is still loading.</summary>
    public bool IsMetadataLoading { get; private set; }

    /// <summary>Why nothing can be edited now; None when it can.</summary>
    public EditBlock Block { get; private set; } = EditBlock.NoVehicle;

    /// <summary>Editing is allowed.</summary>
    public bool CanEdit => Block == EditBlock.None;

    /// <summary>Full-list progress while the parameters are read: received, expected.</summary>
    public (int Received, int Expected)? Progress { get; private set; }

    /// <summary>Reads the shown value (pending, else the vehicle's).</summary>
    public ParameterReader Reader { get; }

    /// <summary>Number of pending changes.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>An Apply is writing.</summary>
    public bool IsApplying => _pending.IsApplying;

    /// <summary>Every parameter the vehicle reported, by name.</summary>
    public IReadOnlyDictionary<string, ParameterEntry> Values => _vehicle?.Parameters.Values ?? new Dictionary<string, ParameterEntry>();

    /// <summary>The vehicle reports <paramref name="name"/>.</summary>
    public bool Has(string name) => Values.ContainsKey(name);

    /// <summary>The vehicle's value of <paramref name="name"/>.</summary>
    public double? Value(string name) => Values.TryGetValue(name, out var entry) ? entry.Value.Value : null;

    /// <summary>The pending value of <paramref name="name"/>.</summary>
    public double? Pending(string name) => _pending[name];

    /// <summary>The pending value, else the vehicle's.</summary>
    public double? Shown(string name) => _pending[name] ?? Value(name);

    /// <summary>The vehicle's type of <paramref name="name"/>.</summary>
    public MavParamType? TypeOf(string name) => Values.TryGetValue(name, out var entry) ? entry.Value.Type : null;

    /// <summary>The metadata of <paramref name="name"/>.</summary>
    public ParameterMetadata? MetadataOf(string name) => Metadata[name];

    /// <summary>Stages a change; false when the vehicle does not report the parameter or nothing can be edited.</summary>
    public bool Set(string name, double value) => CanEdit && _pending.Set(name, value);

    /// <summary>Drops a staged change.</summary>
    public void Revert(string name) => _pending.Remove(name);

    /// <summary>Drops every staged change.</summary>
    public void Discard() => _pending.Discard();

    /// <summary>Writes every staged change.</summary>
    public Task<IReadOnlyList<ParameterApplyResult>> ApplyAsync() => _pending.ApplyAsync();

    /// <summary>Re-reads every parameter from the vehicle.</summary>
    public Task RefreshAsync() => _vehicle?.Parameters.RefreshAsync() ?? Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        _vehicles.ActiveChanged -= OnActiveChanged;
        _metadata.Loaded -= OnMetadataLoaded;
        _pending.Changed -= OnPendingChanged;
        Follow(null);
    }

    private void OnActiveChanged(Vehicle? vehicle) => _ui.Post(() => Follow(vehicle));

    private void Follow(Vehicle? vehicle)
    {
        if (_vehicle is not null)
        {
            _vehicle.Parameters.ValueChanged -= OnValue;
            _vehicle.Parameters.Loaded -= OnLoaded;
            _vehicle.Parameters.Progress -= OnProgress;
            _vehicle.SnapshotChanged -= OnSnapshot;
            _vehicle.Session.ConnectionChanged -= OnConnection;
            _vehicle.HealthChanged -= OnHealth;
        }

        _vehicle = vehicle;
        Progress = null;
        if (vehicle is not null)
        {
            vehicle.Parameters.ValueChanged += OnValue;
            vehicle.Parameters.Loaded += OnLoaded;
            vehicle.Parameters.Progress += OnProgress;
            vehicle.SnapshotChanged += OnSnapshot;
            vehicle.Session.ConnectionChanged += OnConnection;
            vehicle.HealthChanged += OnHealth;
            if (_metadata.For(vehicle.Id) is null && vehicle.Parameters.IsLoaded)
            {
                _ = _metadata.LoadAsync(vehicle);
            }
        }

        Metadata = vehicle is null ? VehicleMetadata.Empty : _metadata.For(vehicle.Id) ?? VehicleMetadata.Empty;
        UpdateState(force: true);
        ValuesChanged?.Invoke(null);
    }

    private void UpdateState(bool force = false)
    {
        var block = ParameterRules.WhyNotEditable(_vehicle);
        var loading = _vehicle is not null && _metadata.For(_vehicle.Id) is null;
        if (!force && block == Block && loading == IsMetadataLoading)
        {
            return;
        }

        Block = block;
        IsMetadataLoading = loading;
        OnPropertyChanged(nameof(Block));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(IsMetadataLoading));
        StateChanged?.Invoke();
    }

    private void OnValue(ParameterEntry entry)
    {
        // During a full read only the progress counts; the list is shown when it completes.
        if (_vehicle?.Parameters.IsRefreshing != true)
        {
            _ui.Post(() => ValuesChanged?.Invoke(entry.Name));
        }
    }

    private void OnLoaded(ParameterListResult result) => _ui.Post(() =>
    {
        Progress = null;
        UpdateState(force: true);
        ValuesChanged?.Invoke(null);
    });

    private void OnProgress(int received, int expected) => _ui.Post(() =>
    {
        var first = Progress is null;
        Progress = (received, expected);
        UpdateState(force: first);
        OnPropertyChanged(nameof(Progress));
    });

    private void OnSnapshot(Vehicles.VehicleSnapshot snapshot)
    {
        if (ParameterRules.WhyNotEditable(_vehicle) != Block)
        {
            _ui.Post(() => UpdateState());
        }
    }

    private void OnConnection(bool connected) => _ui.Post(() => UpdateState());

    private void OnHealth(Events.HealthReport report) => _ui.Post(() => HealthChanged?.Invoke());

    private void OnMetadataLoaded(Vehicles.VehicleId id, VehicleMetadata metadata) => _ui.Post(() =>
    {
        if (_vehicle?.Id == id)
        {
            Metadata = metadata;
            UpdateState(force: true);
            ValuesChanged?.Invoke(null);
        }
    });

    private void OnPendingChanged() => _ui.Post(() =>
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(IsApplying));
        StateChanged?.Invoke();
        ValuesChanged?.Invoke(null);
    });
}
