using Ketras.Model.Vehicles;
using Ketras.Settings;

namespace Ketras.Vehicles;

/// <summary>The inputs of an energy budget, resolved from the vehicle's parameters and the operator's settings.</summary>
/// <param name="CapacityMah">Battery capacity; 0 when unknown.</param>
/// <param name="ReserveFraction">The low-battery threshold, 0…1.</param>
/// <param name="CruiseCurrentAmps">The entered cruise current; 0 when not entered.</param>
/// <param name="Limits">The battery's configured voltages; null when unknown.</param>
public sealed record EnergyInputs(double CapacityMah, double ReserveFraction, double CruiseCurrentAmps, BatteryLimits? Limits);

/// <summary>
/// The operator's energy settings and their resolution. The capacity is PX4's BAT1_CAPACITY when it is set, else the
/// entered one; the reserve is PX4's BAT_LOW_THR (where its low-battery action starts), 15 % without it; the cruise
/// current is entered (no PX4 parameter holds it).
/// </summary>
public static class EnergySettings
{
    /// <summary>The PX4 reserve without BAT_LOW_THR.</summary>
    public const double DefaultReserve = 0.15;

    /// <summary>The current drawn in cruise, A; 0 when not entered.</summary>
    public static SettingDefinition<double> CruiseCurrent { get; } = new("energy.cruise-current", 0);

    /// <summary>The battery capacity used when the vehicle has none set, mAh; 0 when not entered.</summary>
    public static SettingDefinition<double> Capacity { get; } = new("energy.capacity", 0);

    /// <summary>The PX4 parameters read.</summary>
    public static IReadOnlySet<string> Parameters { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "BAT1_CAPACITY", "BAT_LOW_THR", "BAT1_N_CELLS", "BAT1_V_EMPTY", "BAT1_V_CHARGED"
    };

    /// <summary>Resolves the inputs for <paramref name="vehicle"/> (null: the settings alone).</summary>
    public static EnergyInputs Resolve(Vehicle? vehicle, SettingsStore settings)
    {
        double? Parameter(string name) => vehicle?.Parameters[name];
        var capacity = Parameter("BAT1_CAPACITY") is > 0 and var fromVehicle ? fromVehicle : settings.Get(Capacity);
        var reserve = Parameter("BAT_LOW_THR") is >= 0 and < 1 and var threshold ? threshold : DefaultReserve;
        var limits = Parameter("BAT1_N_CELLS") is > 0 and var cells && Parameter("BAT1_V_EMPTY") is { } empty && Parameter("BAT1_V_CHARGED") is { } charged
            ? new BatteryLimits((int)cells, empty, charged)
            : null;
        return new EnergyInputs(Math.Max(0, capacity), reserve, Math.Max(0, settings.Get(CruiseCurrent)), limits);
    }
}
