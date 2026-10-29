using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Parameters;
using Ketras.Resources;
using Ketras.Station.Settings;
using Ketras.Station.Vehicles;
using Ketras.Utilities;

namespace Ketras.Setup;

/// <summary>A titled set of parameter rows on a task page; rows the vehicle does not report are hidden.</summary>
public sealed partial class ParameterSectionViewModel(string title, IReadOnlyList<ParameterFieldViewModel> fields, Func<bool>? visible = null) : ObservableObject
{
    /// <summary>The title.</summary>
    public string Title { get; } = title;

    /// <summary>All rows.</summary>
    public IReadOnlyList<ParameterFieldViewModel> Fields { get; } = fields;

    /// <summary>The rows shown.</summary>
    [ObservableProperty] public partial IReadOnlyList<ParameterFieldViewModel> Shown { get; private set; } = [];

    /// <summary>The section shows.</summary>
    [ObservableProperty] public partial bool IsVisible { get; private set; }

    /// <summary>Refreshes the rows and what shows; <paramref name="rowVisible"/> may hide rows that do exist.</summary>
    public void Refresh(Func<ParameterFieldViewModel, bool>? rowVisible = null)
    {
        foreach (var field in Fields)
        {
            field.Refresh();
        }

        Shown = [.. Fields.Where(f => f.Exists && (rowVisible?.Invoke(f) ?? true))];
        IsVisible = Shown.Count > 0 && (visible?.Invoke() ?? true);
    }
}

/// <summary>
/// A battery factor calculator: the operator types what an external meter shows, and the
/// new <c>BAT1_V_DIV</c> or <c>BAT1_A_PER_V</c> (measured × current factor ÷ the vehicle's reading) is staged.
/// </summary>
public sealed partial class BatteryCalculatorViewModel(SetupContext context, string parameter, bool voltage) : ObservableObject
{
    private double? _reading;

    /// <summary>The factor it computes.</summary>
    public string Parameter { get; } = parameter;

    /// <summary>"External voltmeter" or "External ammeter".</summary>
    public string Label => voltage ? Strings.PowerVoltmeter : Strings.PowerAmmeter;

    /// <summary>The meter's value typed by the operator.</summary>
    [ObservableProperty] public partial string Measured { get; set; } = string.Empty;

    /// <summary>"vehicle reads 12.31 V".</summary>
    [ObservableProperty] public partial string ReadingText { get; private set; } = Strings.PowerNoReading;

    /// <summary>Why it did not compute; empty otherwise.</summary>
    [ObservableProperty] public partial string Problem { get; private set; } = string.Empty;

    /// <summary>It shows (the vehicle has the factor).</summary>
    [ObservableProperty] public partial bool IsVisible { get; private set; }

    /// <summary>Calculate is possible.</summary>
    [ObservableProperty] public partial bool CanCalculate { get; private set; }

    /// <summary>Takes the vehicle's reading.</summary>
    public void Apply(Mavlink.Vehicles.BatteryReading? battery)
    {
        _reading = voltage ? battery?.VoltageVolts : battery?.CurrentAmps;
        ReadingText = _reading is { } value
            ? string.Format(CultureInfo.CurrentCulture, Strings.PowerReads, value.ToString("F2", CultureInfo.CurrentCulture), voltage ? "V" : "A")
            : Strings.PowerNoReading;
        Refresh();
    }

    /// <summary>Re-evaluates visibility and Calculate.</summary>
    public void Refresh()
    {
        IsVisible = context.Has(Parameter);
        CanCalculate = IsVisible && context.CanEdit && _reading is > 0;
    }

    [RelayCommand]
    private void Calculate()
    {
        Problem = string.Empty;
        if (!double.TryParse(Measured, NumberStyles.Float, CultureInfo.CurrentCulture, out var measured) || measured <= 0)
        {
            Problem = Strings.FieldNotNumber;
            return;
        }

        if (_reading is not { } reading || context.Shown(Parameter) is not { } current
            || Station.Setup.RcRules.BatteryFactor(measured, current, reading) is not { } factor)
        {
            Problem = Strings.PowerNoReading;
            return;
        }

        context.Set(Parameter, factor);
    }
}

/// <summary>
/// The Power page: each battery's source, cells and voltages (batteries 2 and 3 when the vehicle has them; the rest of a
/// battery only while its source is on), and the low-battery levels and failsafe action, so the battery is set up in one
/// place; and the energy estimate's inputs that no parameter holds (the cruise current, a capacity for a vehicle without
/// BAT1_CAPACITY), stored in the operator's settings and usable without a vehicle.
/// </summary>
public sealed class PowerPageViewModel : IDisposable
{
    private static readonly string[] BatteryFields = ["N_CELLS", "CAPACITY", "V_EMPTY", "V_CHARGED", "R_INTERNAL", "V_DIV", "A_PER_V"];

    private readonly SetupContext _context;
    private readonly IUiDispatcher _ui;
    private Vehicle? _vehicle;

    /// <summary>The cruise current for the energy estimate.</summary>
    public Planning.FieldViewModel CruiseCurrent { get; }

    /// <summary>The capacity used while the vehicle has none set.</summary>
    public Planning.FieldViewModel Capacity { get; }

    /// <summary>Follows <paramref name="context"/> and the vehicle's battery reading.</summary>
    public PowerPageViewModel(SetupContext context, IUiDispatcher ui, SettingsStore settings, TimeProvider time)
    {
        _context = context;
        _ui = ui;
        CruiseCurrent = new Planning.FieldViewModel("cruise-current", Strings.PowerCruiseCurrent, "A", settings.Get(EnergySettings.CruiseCurrent), 1, 0, 500,
            value =>
            {
                settings.Set(EnergySettings.CruiseCurrent, value);
                return true;
            }, ui, time) { Help = Strings.PowerCruiseCurrentHelp };
        Capacity = new Planning.FieldViewModel("capacity", Strings.PowerCapacity, "mAh", settings.Get(EnergySettings.Capacity), 0, 0, 1_000_000,
            value =>
            {
                settings.Set(EnergySettings.Capacity, value);
                return true;
            }, ui, time) { Help = Strings.PowerCapacityHelp };
        Calculators = [new BatteryCalculatorViewModel(context, "BAT1_V_DIV", voltage: true), new BatteryCalculatorViewModel(context, "BAT1_A_PER_V", voltage: false)];
        Sections =
        [
            .. Enumerable.Range(1, 3).Select(Battery),
            new ParameterSectionViewModel(Strings.PowerFailsafe,
            [
                Field("COM_LOW_BAT_ACT"), Field("BAT_LOW_THR"), Field("BAT_CRIT_THR"), Field("BAT_EMERGEN_THR")
            ])
        ];
        context.ValuesChanged += _ => Refresh();
        context.StateChanged += Refresh;
        Refresh();
    }

    /// <summary>The page title.</summary>
    public string Title => Strings.SetupPagePower;

    /// <summary>The sections.</summary>
    public IReadOnlyList<ParameterSectionViewModel> Sections { get; }

    /// <summary>Battery 1's voltage divider and amps-per-volt calculators.</summary>
    public IReadOnlyList<BatteryCalculatorViewModel> Calculators { get; }

    /// <inheritdoc />
    public void Dispose() => Follow(null);

    /// <summary>Refreshes every section.</summary>
    public void Refresh()
    {
        if (!ReferenceEquals(_context.Vehicle, _vehicle))
        {
            Follow(_context.Vehicle);
        }

        foreach (var calculator in Calculators)
        {
            calculator.Refresh();
        }

        foreach (var section in Sections)
        {
            section.Refresh(field =>
            {
                // A battery's details show only while its source is on.
                var underscore = field.Name.IndexOf('_', StringComparison.Ordinal);
                return !field.Name.StartsWith("BAT", StringComparison.Ordinal) || underscore < 0 || field.Name.EndsWith("_SOURCE", StringComparison.Ordinal)
                    || field.Name.StartsWith("BAT_", StringComparison.Ordinal)
                    || _context.Shown(field.Name[..underscore] + "_SOURCE") is >= 0;
            });
        }
    }

    private void Follow(Vehicle? vehicle)
    {
        if (_vehicle is not null)
        {
            _vehicle.Session.Status.Battery -= OnBattery;
        }

        _vehicle = vehicle;
        foreach (var calculator in Calculators)
        {
            calculator.Apply(null);
        }

        if (vehicle is not null)
        {
            vehicle.Session.Status.Battery += OnBattery;
        }
    }

    private void OnBattery(Mavlink.Vehicles.BatteryReading reading) => _ui.Post(() =>
    {
        foreach (var calculator in Calculators)
        {
            calculator.Apply(reading);
        }
    });

    private ParameterSectionViewModel Battery(int index)
    {
        var prefix = string.Create(CultureInfo.InvariantCulture, $"BAT{index}_");
        return new ParameterSectionViewModel(string.Format(CultureInfo.CurrentCulture, Strings.PowerBattery, index),
            [Field(prefix + "SOURCE"), .. BatteryFields.Select(f => Field(prefix + f))]);
    }

    private ParameterFieldViewModel Field(string name) => new(name, _context);
}
