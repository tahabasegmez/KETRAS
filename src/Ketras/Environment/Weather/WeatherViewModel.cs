using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Model.Environment;
using Ketras.Resources;
using Ketras.Station.Environment;
using Ketras.Utilities;

namespace Ketras.Environment.Weather;

/// <summary>
/// The weather panel: the latest sample any environment provider reported (the vehicle's own wind and air, or a
/// plugin's), with its source and time. Wind is the direction it comes from. The panel collapses to its header.
/// </summary>
public sealed partial class WeatherViewModel : ObservableObject, IDisposable
{
    private const string None = "--";
    private static readonly string[] Points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    private readonly EnvironmentStore _environment;
    private readonly UnitService _units;
    private readonly IUiDispatcher _ui;

    /// <summary>Follows <paramref name="environment"/>.</summary>
    public WeatherViewModel(EnvironmentStore environment, UnitService units, IUiDispatcher ui)
    {
        _environment = environment;
        _units = units;
        _ui = ui;
        _environment.Changed += OnChanged;
        Refresh();
    }

    /// <summary>The body is shown.</summary>
    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;

    /// <summary>A sample is shown.</summary>
    [ObservableProperty] public partial bool HasData { get; private set; }

    /// <summary>"Vehicle · 12:04:31", or "no data".</summary>
    [ObservableProperty] public partial string Status { get; private set; } = Strings.WeatherNoData;

    /// <summary>Wind direction, degrees from.</summary>
    [ObservableProperty] public partial double? WindFrom { get; private set; }

    /// <summary>Wind speed, m/s.</summary>
    [ObservableProperty] public partial double? WindSpeed { get; private set; }

    /// <summary>Temperature in large digits.</summary>
    [ObservableProperty] public partial string Temperature { get; private set; } = None;

    /// <summary>"6.2 m/s from 220° SW".</summary>
    [ObservableProperty] public partial string Wind { get; private set; } = None;

    /// <summary>Vertical wind.</summary>
    [ObservableProperty] public partial string VerticalWind { get; private set; } = None;

    /// <summary>Relative humidity.</summary>
    [ObservableProperty] public partial string Humidity { get; private set; } = None;

    /// <summary>Air pressure.</summary>
    [ObservableProperty] public partial string Pressure { get; private set; } = None;

    /// <summary>The compass point of a direction, e.g. 220 → "SW".</summary>
    internal static string CompassPoint(double degrees) => Points[(int)Math.Round(((degrees % 360) + 360) % 360 / 45) % 8];

    /// <inheritdoc />
    public void Dispose() => _environment.Changed -= OnChanged;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    private void OnChanged(EnvironmentChange change)
    {
        if (change.HasFlag(EnvironmentChange.Weather))
        {
            _ui.Post(Refresh);
        }
    }

    private void Refresh()
    {
        var sample = _environment.Weather.OrderByDescending(w => w.TimeUtc).FirstOrDefault();
        Apply(sample);
    }

    private void Apply(WeatherSample? sample)
    {
        var culture = CultureInfo.CurrentCulture;
        HasData = sample is not null;
        Status = sample is null ? Strings.WeatherNoData : $"{sample.Source} · {sample.TimeUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";
        WindFrom = sample?.WindFromDegrees;
        WindSpeed = sample?.WindSpeedMps;
        Temperature = sample?.TemperatureCelsius is { } t ? _units.Temperature(t) : None;
        Wind = sample?.WindSpeedMps is { } speed
            ? sample.WindFromDegrees is { } from
                ? string.Format(culture, Strings.WeatherWind, _units.Speed(speed), from, CompassPoint(from))
                : _units.Speed(speed)
            : None;
        VerticalWind = sample?.VerticalWindMps is { } vertical ? _units.Speed(vertical) : None;
        Humidity = sample?.HumidityPercent is { } humidity ? string.Format(culture, "{0:F0} %", humidity) : None;
        Pressure = sample?.PressureHectopascals is { } pressure ? string.Format(culture, "{0:F1} hPa", pressure) : None;
    }
}
