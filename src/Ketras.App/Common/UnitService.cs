using System.Globalization;

namespace Ketras.App.Common;

/// <summary>Unit systems the station can show; metric only for now.</summary>
public enum UnitSystem
{
    /// <summary>Metres, metres per second, degrees Celsius.</summary>
    Metric
}

/// <summary>
/// Service: the one place that turns SI values into text for the operator, so a unit system can be added without
/// touching the screens. Values stay SI everywhere else.
/// </summary>
public sealed class UnitService
{
    /// <summary>The unit system in use.</summary>
    public UnitSystem System => UnitSystem.Metric;

    /// <summary>A distance, e.g. "850 m" or "12.40 km".</summary>
    public string Distance(double meters) =>
        Math.Abs(meters) < 1000 ? Format("{0:F0} m", meters) : Format("{0:F2} km", meters / 1000);

    /// <summary>An altitude or height, e.g. "120 m".</summary>
    public string Altitude(double meters) => Format("{0:F0} m", meters);

    /// <summary>A speed, e.g. "18.5 m/s".</summary>
    public string Speed(double metersPerSecond) => Format("{0:F1} m/s", metersPerSecond);

    /// <summary>A temperature, e.g. "21.5 °C".</summary>
    public string Temperature(double celsius) => Format("{0:F1} °C", celsius);

    private static string Format(string format, double value) => string.Format(CultureInfo.CurrentCulture, format, value);
}
