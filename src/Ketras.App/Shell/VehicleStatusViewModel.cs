using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.App.Common;
using Ketras.Station.Vehicles;

namespace Ketras.App.Shell;

/// <summary>A vehicle the status bar can switch to.</summary>
/// <param name="Vehicle">The vehicle.</param>
/// <param name="IsActive">It is the active one.</param>
public sealed record VehicleChoice(Vehicle Vehicle, bool IsActive)
{
    /// <summary>Its name.</summary>
    public string Name => Vehicle.Name;
}

/// <summary>
/// The vehicle identity item of the status bar (on the Accent fill): the active vehicle's name; with several vehicles on
/// the link, a press lists them to switch.
/// </summary>
public sealed partial class VehicleStatusViewModel : ObservableObject, IDisposable
{
    private readonly VehicleRegistry _vehicles;
    private readonly IUiDispatcher _ui;

    /// <summary>Follows <paramref name="vehicles"/>.</summary>
    public VehicleStatusViewModel(VehicleRegistry vehicles, IUiDispatcher ui)
    {
        _vehicles = vehicles;
        _ui = ui;
        _vehicles.VehicleAdded += OnVehiclesChanged;
        _vehicles.VehicleRemoved += OnVehiclesChanged;
        _vehicles.ActiveChanged += OnActiveChanged;
        Refresh();
    }

    /// <summary>Every vehicle on the link.</summary>
    public ObservableCollection<VehicleChoice> Choices { get; } = [];

    /// <summary>The active vehicle's name; empty without one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVehicle))]
    public partial string ActiveName { get; private set; } = string.Empty;

    /// <summary>True while there are several vehicles to choose from.</summary>
    [ObservableProperty]
    public partial bool CanSwitch { get; private set; }

    /// <summary>True while a vehicle is active.</summary>
    public bool HasVehicle => ActiveName.Length > 0;

    /// <summary>Makes a vehicle the active one.</summary>
    [RelayCommand]
    private void Select(VehicleChoice choice) => _vehicles.SetActive(choice.Vehicle.Id);

    /// <inheritdoc />
    public void Dispose()
    {
        _vehicles.VehicleAdded -= OnVehiclesChanged;
        _vehicles.VehicleRemoved -= OnVehiclesChanged;
        _vehicles.ActiveChanged -= OnActiveChanged;
    }

    private void OnVehiclesChanged(Vehicle vehicle) => _ui.Post(Refresh);

    private void OnActiveChanged(Vehicle? vehicle) => _ui.Post(Refresh);

    private void Refresh()
    {
        var active = _vehicles.Active;
        Choices.Clear();
        foreach (var vehicle in _vehicles.Vehicles)
        {
            Choices.Add(new VehicleChoice(vehicle, vehicle == active));
        }

        ActiveName = active?.Name ?? string.Empty;
        CanSwitch = Choices.Count > 1;
    }
}
