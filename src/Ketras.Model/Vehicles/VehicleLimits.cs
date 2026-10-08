namespace Ketras.Model.Vehicles;

/// <summary>
/// The vehicle's performance envelope a plan is checked against; <see cref="Default"/> holds PX4's fixed-wing defaults.
/// The station derives the values from the vehicle's parameters.
/// </summary>
public sealed record VehicleLimits
{
    /// <summary>PX4 fixed-wing defaults.</summary>
    public static VehicleLimits Default { get; } = new();

    /// <summary>Lowest airspeed the vehicle flies, already scaled for its current weight.</summary>
    public double MinAirspeedMps { get; init; } = 10;

    /// <summary>Highest airspeed.</summary>
    public double MaxAirspeedMps { get; init; } = 20;

    /// <summary>Highest bank angle.</summary>
    public double MaxBankDegrees { get; init; } = 50;

    /// <summary>Steepest glide slope the vehicle accepts for a landing approach.</summary>
    public double MaxLandingSlopeDegrees { get; init; } = 5;

    /// <summary>Orbit radius of new loiter items.</summary>
    public double DefaultLoiterRadiusMeters { get; init; } = 80;

    /// <summary>Highest climb rate the vehicle commands.</summary>
    public double MaxClimbRateMps { get; init; } = 5;

    /// <summary>Highest sink rate outside a landing.</summary>
    public double MaxSinkRateMps { get; init; } = 5;

    /// <summary>Height above home the vehicle returns at; null skips the return-path terrain check.</summary>
    public double? ReturnAltitudeMeters { get; init; }
}
