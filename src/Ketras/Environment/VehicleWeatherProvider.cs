using Ketras.Mavlink.Vehicles;
using Ketras.Model.Environment;
using Ketras.Vehicles;

namespace Ketras.Environment;

/// <summary>Weather the active vehicle measures: its wind estimate (WIND_COV) and ambient air (HYGROMETER_SENSOR).</summary>
public sealed class VehicleWeatherProvider(VehicleRegistry vehicles, TimeProvider time) : IEnvironmentProvider
{
    private readonly object _lock = new();
    private IEnvironmentSink? _sink;
    private VehicleStatus? _status;
    private WindEstimate? _wind;
    private AirSample? _air;

    /// <inheritdoc />
    public string Name => "Vehicle";

    /// <inheritdoc />
    public Task StartAsync(IEnvironmentSink sink, CancellationToken cancellation)
    {
        _sink = sink;
        vehicles.ActiveChanged += Follow;
        Follow(vehicles.Active);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellation)
    {
        vehicles.ActiveChanged -= Follow;
        Follow(null);
        _sink = null;
        return Task.CompletedTask;
    }

    private void Follow(Vehicle? vehicle)
    {
        lock (_lock)
        {
            if (_status is not null)
            {
                _status.WindChanged -= OnWind;
                _status.AirChanged -= OnAir;
            }

            _status = vehicle?.Session.Status;
            _wind = null;
            _air = null;
            if (_status is not null)
            {
                _status.WindChanged += OnWind;
                _status.AirChanged += OnAir;
            }
        }
    }

    private void OnWind(WindEstimate wind)
    {
        lock (_lock)
        {
            _wind = wind;
        }

        Report();
    }

    private void OnAir(AirSample air)
    {
        lock (_lock)
        {
            _air = air;
        }

        Report();
    }

    private void Report()
    {
        WindEstimate? wind;
        AirSample? air;
        lock (_lock)
        {
            wind = _wind;
            air = _air;
        }

        _sink?.ReportWeather(new WeatherSample
        {
            Source = Name,
            TimeUtc = time.GetUtcNow().UtcDateTime,
            WindFromDegrees = wind?.FromDegrees,
            WindSpeedMps = wind?.SpeedMps,
            VerticalWindMps = wind?.DownMps is { } down ? -down : null,
            TemperatureCelsius = air?.TemperatureCelsius,
            HumidityPercent = air?.HumidityPercent
        });
    }
}
