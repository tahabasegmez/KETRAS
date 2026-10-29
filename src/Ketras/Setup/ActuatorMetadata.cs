namespace Ketras.Setup;

/// <summary>What PX4's <c>actuators.json</c> (MAVLink component metadata, actuators type) describes:
/// output functions, the geometry of each airframe type, the rules between geometry fields and the
/// output groups of the board. Describes only; which parameters exist is decided by the vehicle.</summary>
public sealed class ActuatorMetadata
{
    /// <summary>Output functions by id.</summary>
    public required IReadOnlyDictionary<int, OutputFunctionInfo> Functions { get; init; }
    /// <summary>Actuator types by name ("motor", "servo", "DEFAULT").</summary>
    public required IReadOnlyDictionary<string, ActuatorTypeInfo> ActuatorTypes { get; init; }
    /// <summary>The airframe geometries.</summary>
    public required IReadOnlyList<MixerConfigInfo> MixerConfigs { get; init; }
    /// <summary>Rules between geometry fields.</summary>
    public required IReadOnlyList<MixerRuleInfo> Rules { get; init; }
    /// <summary>The board's output drivers.</summary>
    public required IReadOnlyList<OutputGroupInfo> Outputs { get; init; }

    /// <summary>Top-level <c>show-ui-if</c>; empty = always.</summary>
    public string ShowUiIf { get; init; } = string.Empty;
}

/// <summary>One output function (<c>functions_v1</c>), e.g. 101 = Motor 1, 201 = Servo 1.</summary>
public sealed record OutputFunctionInfo(int Id, string Label, bool ExcludeFromTesting, string NoteCondition, string NoteText);

/// <summary>A parameter reference inside the metadata. <see cref="Name"/> may contain <c>${i}</c>;
/// <see cref="Role"/> is the JSON <c>function</c> key (type, function, disarmed, min, max, failsafe,
/// enable, primary...).</summary>
public sealed record ActuatorParamInfo(
    string Name,
    string Label,
    bool Advanced,
    string Role,
    string Identifier,
    int IndexOffset,
    ParamShowAs ShowAs,
    string ShowIf,
    IReadOnlyList<double> FixedValues);

/// <summary>How a field is shown.</summary>
public enum ParamShowAs
{
    /// <summary>Its value.</summary>
    Value,

    /// <summary>One bit of a shared integer.</summary>
    Bitset,

    /// <summary>A check box, checked while the value is positive.</summary>
    TrueIfPositive
}

/// <summary><c>mixer_v1.actuator-types</c> entry: function range and test value range.</summary>
public sealed record ActuatorTypeInfo(
    string Name,
    int? FunctionMin,
    int? FunctionMax,
    double Min,
    double Max,
    double Default,
    bool DefaultIsNan,
    int LabelIndexOffset,
    IReadOnlyList<ActuatorParamInfo> PerItemParameters)
{
    /// <summary>The function belongs to this type.</summary>
    public bool Contains(int function) => FunctionMin is { } min && FunctionMax is { } max && function >= min && function <= max;
}

/// <summary>One airframe geometry (<c>mixer_v1.config</c>), selected by its <see cref="Option"/> condition.</summary>
public sealed record MixerConfigInfo(string Option, string Title, string HelpUrl, IReadOnlyList<MixerGroupInfo> Groups);

/// <summary>A group of actuators of one type in a geometry, e.g. "Control Surfaces".</summary>
public sealed record MixerGroupInfo(
    string ActuatorType,
    string CountParam,
    int FixedCount,
    string GroupLabel,
    bool Required,
    IReadOnlyList<string> ItemLabelPrefix,
    IReadOnlyList<ActuatorParamInfo> Parameters,
    IReadOnlyList<ActuatorParamInfo> PerItemParameters);

/// <summary>Constraints the value of one per-item field (the select identifier, e.g. the surface type)
/// puts on other per-item fields.</summary>
public sealed record MixerRuleInfo(string SelectIdentifier, IReadOnlyList<string> ApplyIdentifiers, IReadOnlyDictionary<int, IReadOnlyList<RuleItemInfo>> Items);

/// <summary>What a rule does to one field for one select value.</summary>
public sealed record RuleItemInfo(double? Default, double? Min, double? Max, bool Hidden, bool Disabled);

/// <summary>An output driver (<c>outputs_v1</c>), e.g. "PWM MAIN" or "SIM_GZ".</summary>
public sealed record OutputGroupInfo(string Label, string ShowSubgroupsIf, IReadOnlyList<ActuatorParamInfo> Parameters, IReadOnlyList<OutputSubgroupInfo> Subgroups);

/// <summary>A subgroup of an output driver (e.g. timer group).</summary>
public sealed record OutputSubgroupInfo(
    string Label,
    IReadOnlyList<ActuatorParamInfo> Parameters,
    IReadOnlyList<ActuatorParamInfo> PerChannelParameters,
    IReadOnlyList<OutputChannelInfo> Channels);

/// <summary>One output channel.</summary>
public sealed record OutputChannelInfo(string Label, int ParamIndex);
