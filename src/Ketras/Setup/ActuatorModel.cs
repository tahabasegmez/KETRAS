using System.Globalization;
using Ketras.Parameters;

namespace Ketras.Setup;

/// <summary>A parameter shown by the Actuators page; <see cref="Bit"/> is set for one bit of a shared integer.</summary>
/// <param name="Param">The parameter.</param>
/// <param name="Label">Its label from the metadata.</param>
/// <param name="Advanced">Shown only in advanced mode.</param>
/// <param name="Bit">The bit, for a bitset field.</param>
/// <param name="TrueIfPositive">A check box, checked while the value is positive.</param>
public sealed record ActuatorField(string Param, string Label, bool Advanced, int? Bit = null, bool TrueIfPositive = false)
{
    /// <summary>A rule does not hide it.</summary>
    public bool Visible { get; init; } = true;

    /// <summary>A rule does not disable it.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The rule's minimum.</summary>
    public double? Min { get; init; }

    /// <summary>The rule's maximum.</summary>
    public double? Max { get; init; }
}

/// <summary>One geometry column (a per-item field), e.g. "Type" or "Roll Torque".</summary>
/// <param name="Label">Its label.</param>
/// <param name="Advanced">Shown only in advanced mode.</param>
/// <param name="Identifier">The rules' identifier.</param>
/// <param name="IsType">It is the surface type.</param>
public sealed record GeometryColumn(string Label, bool Advanced, string Identifier, bool IsType);

/// <summary>One actuator of a geometry group, with a cell per column (null where the vehicle has no such parameter).</summary>
/// <param name="Label">"Servo 1 (Left Aileron)".</param>
/// <param name="Function">Its output function.</param>
/// <param name="Index">Its index in the group.</param>
/// <param name="Cells">One per column.</param>
public sealed record GeometryItem(string Label, int Function, int Index, IReadOnlyList<ActuatorField?> Cells);

/// <summary>A group of the selected geometry, e.g. "Control Surfaces".</summary>
/// <param name="Label">Its label.</param>
/// <param name="ActuatorType">"motor" or "servo".</param>
/// <param name="Required">Its functions must be assigned to outputs.</param>
/// <param name="Count">The count parameter, if any.</param>
/// <param name="Columns">Per-item columns.</param>
/// <param name="Items">The actuators.</param>
/// <param name="Parameters">Group-wide parameters.</param>
/// <param name="Rule">The rule its type column selects, if any.</param>
public sealed record ActuatorGroup(
    string Label,
    string ActuatorType,
    bool Required,
    ActuatorField? Count,
    IReadOnlyList<GeometryColumn> Columns,
    IReadOnlyList<GeometryItem> Items,
    IReadOnlyList<ActuatorField> Parameters,
    MixerRuleInfo? Rule);

/// <summary>A column of an output channel table.</summary>
/// <param name="Label">Its label.</param>
/// <param name="Advanced">Shown only in advanced mode.</param>
/// <param name="Role">"function", "disarmed", "min", "max", "failsafe"…</param>
public sealed record OutputChannelColumn(string Label, bool Advanced, string Role);

/// <summary>One output channel.</summary>
/// <param name="Label">"MAIN 3".</param>
/// <param name="ParamIndex">Its index in the parameter names.</param>
/// <param name="Function">The function assigned; 0 when none.</param>
/// <param name="Cells">One per column.</param>
public sealed record OutputChannel(string Label, int ParamIndex, int Function, IReadOnlyList<ActuatorField?> Cells)
{
    /// <summary>A function is assigned.</summary>
    public bool IsAssigned => Function != 0;
}

/// <summary>A subgroup of an output driver.</summary>
/// <param name="Label">Its label.</param>
/// <param name="Parameters">Its own parameters (e.g. the PWM rate).</param>
/// <param name="Columns">The channel table's columns.</param>
/// <param name="Channels">The channels.</param>
public sealed record OutputSubgroup(string Label, IReadOnlyList<ActuatorField> Parameters, IReadOnlyList<OutputChannelColumn> Columns, IReadOnlyList<OutputChannel> Channels);

/// <summary>An output driver, e.g. "PWM MAIN".</summary>
/// <param name="Label">Its label.</param>
/// <param name="Parameters">Its parameters (enable…).</param>
/// <param name="SubgroupsVisible">Its subgroups show (it is enabled).</param>
/// <param name="Subgroups">Its subgroups.</param>
public sealed record OutputGroup(string Label, IReadOnlyList<ActuatorField> Parameters, bool SubgroupsVisible, IReadOnlyList<OutputSubgroup> Subgroups);

/// <summary>An actuator the operator can test, with the value range of its actuator type.</summary>
/// <param name="Label">"Left Aileron".</param>
/// <param name="Function">Its output function.</param>
/// <param name="Min">Lowest test value.</param>
/// <param name="Max">Highest test value.</param>
/// <param name="Default">Where its slider starts.</param>
/// <param name="IsMotor">It is a motor.</param>
/// <param name="IsControlSurface">It is a control surface.</param>
public sealed record TestActuator(string Label, int Function, double Min, double Max, double Default, bool IsMotor, bool IsControlSurface);

/// <summary>
/// Actuator setup resolved against the vehicle's values: the geometry of the selected airframe type, the output groups
/// and the actuators that can be tested. Pure; rebuilt on every value change. Everything comes from the firmware's actuator metadata, so any airframe the
/// firmware describes (fixed-wing, flying wing, VTOL, multirotor) and any output driver is shown the same way.
/// </summary>
public sealed class ActuatorModel
{
    private const string SurfaceType = "servo";
    private const string MotorType = "motor";

    /// <summary>No metadata: nothing to show.</summary>
    public static ActuatorModel Empty { get; } = new()
    {
        HasMetadata = false,
        GeometryTitle = string.Empty,
        Geometry = [],
        Outputs = [],
        TestActuators = [],
        HasUnsetRequiredFunctions = false,
        Notes = [],
        GeometryLabels = new Dictionary<int, string>()
    };

    /// <summary>Built from metadata.</summary>
    public required bool HasMetadata { get; init; }

    /// <summary>Title of the selected geometry ("Fixed Wing"), empty when none applies.</summary>
    public required string GeometryTitle { get; init; }

    /// <summary>The selected geometry's groups.</summary>
    public required IReadOnlyList<ActuatorGroup> Geometry { get; init; }

    /// <summary>The output drivers the vehicle has.</summary>
    public required IReadOnlyList<OutputGroup> Outputs { get; init; }

    /// <summary>The assigned functions that can be tested.</summary>
    public required IReadOnlyList<TestActuator> TestActuators { get; init; }

    /// <summary>A required geometry function (e.g. Motor 1) is not assigned to any output.</summary>
    public required bool HasUnsetRequiredFunctions { get; init; }

    /// <summary>Notes of assigned functions whose condition holds.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>Geometry label of each geometry function, e.g. 201 → "Left Aileron".</summary>
    public required IReadOnlyDictionary<int, string> GeometryLabels { get; init; }

    /// <summary>Builds the model; <paramref name="enumText"/> names an enum value of a parameter (for the surface-type labels).</summary>
    public static ActuatorModel Build(ActuatorMetadata? metadata, ParameterReader lookup, Func<string, long, string?> enumText)
    {
        if (metadata is null || !ActuatorCondition.Evaluate(metadata.ShowUiIf, lookup))
        {
            return Empty;
        }

        var config = metadata.MixerConfigs.FirstOrDefault(c => ActuatorCondition.Evaluate(c.Option, lookup));
        var geometry = config is null ? [] : BuildGeometry(metadata, config, lookup);
        var outputs = BuildOutputs(metadata, lookup);

        var assigned = outputs.SelectMany(o => o.Subgroups).SelectMany(s => s.Channels).Where(c => c.IsAssigned).Select(c => c.Function).Distinct().Order().ToList();
        var tests = new List<TestActuator>();
        foreach (var function in assigned)
        {
            var info = metadata.Functions.GetValueOrDefault(function);
            if (info?.ExcludeFromTesting == true)
            {
                continue;
            }

            var type = metadata.ActuatorTypes.Values.FirstOrDefault(t => t.Contains(function)) ?? metadata.ActuatorTypes.GetValueOrDefault("DEFAULT");
            if (type is null)
            {
                continue;
            }

            var isMotor = type.Name == MotorType;
            tests.Add(new TestActuator(SpecificLabel(function, geometry, info?.Label, lookup, enumText), function, type.Min, type.Max,
                isMotor || type.DefaultIsNan ? type.Min : type.Default, isMotor, IsSurface(function, geometry)));
        }

        var required = geometry.Where(g => g.Required).SelectMany(g => g.Items).Select(i => i.Function).Where(f => f != 0);
        var notes = assigned
            .Select(f => metadata.Functions.GetValueOrDefault(f))
            .Where(f => f is { NoteText.Length: > 0 } && ActuatorCondition.Evaluate(f.NoteCondition, lookup))
            .Select(f => f!.NoteText)
            .ToList();

        return new ActuatorModel
        {
            HasMetadata = true,
            GeometryTitle = config?.Title ?? string.Empty,
            Geometry = geometry,
            Outputs = outputs,
            TestActuators = tests,
            HasUnsetRequiredFunctions = required.Any(f => !assigned.Contains(f)),
            Notes = notes,
            GeometryLabels = GeometryLabelsOf(geometry, lookup, enumText)
        };
    }

    /// <summary>
    /// <paramref name="newValue"/>: the rule's defaults, then clamped to its min / max (<c>Mixer.cc</c>
    /// <c>MixerChannel::applyRule</c>). Only values that differ from the current ones are returned.
    /// </summary>
    public static IReadOnlyList<(string Param, double Value)> RuleWrites(ActuatorGroup group, GeometryItem item, long newValue, ParameterReader lookup)
    {
        if (group.Rule is not { } rule || !rule.Items.TryGetValue((int)newValue, out var ruleItems))
        {
            return [];
        }

        var writes = new List<(string, double)>();
        for (var column = 0; column < group.Columns.Count; column++)
        {
            var applyIndex = IndexOf(rule.ApplyIdentifiers, group.Columns[column].Identifier);
            if (applyIndex < 0 || applyIndex >= ruleItems.Count || item.Cells[column] is not { Bit: null } cell || lookup(cell.Param) is not { } current)
            {
                continue;
            }

            var ruleItem = ruleItems[applyIndex];
            var value = current;
            if (ruleItem.Min is { } min && value < min)
            {
                value = min;
            }

            if (ruleItem.Max is { } max && value > max)
            {
                value = max;
            }

            if (ruleItem.Default is { } defaultValue)
            {
                value = defaultValue;
            }

            if (Math.Abs(value - current) > 1e-6)
            {
                writes.Add((cell.Param, value));
            }
        }

        return writes;
    }

    /// <summary>The channel's output-function field, or null when the vehicle has none.</summary>
    public static ActuatorField? FunctionCell(OutputSubgroup subgroup, OutputChannel channel)
    {
        for (var i = 0; i < subgroup.Columns.Count; i++)
        {
            if (subgroup.Columns[i].Role == "function")
            {
                return channel.Cells[i];
            }
        }

        return null;
    }

    private static Dictionary<int, string> GeometryLabelsOf(IReadOnlyList<ActuatorGroup> geometry, ParameterReader lookup, Func<string, long, string?> enumText)
    {
        var labels = new Dictionary<int, string>();
        foreach (var item in geometry.SelectMany(g => g.Items).Where(i => i.Function != 0))
        {
            labels.TryAdd(item.Function, SpecificLabel(item.Function, geometry, item.Label, lookup, enumText));
        }

        return labels;
    }

    private static List<ActuatorGroup> BuildGeometry(ActuatorMetadata metadata, MixerConfigInfo config, ParameterReader lookup)
    {
        var groups = new List<ActuatorGroup>();
        var typeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in config.Groups)
        {
            var actuatorType = metadata.ActuatorTypes.GetValueOrDefault(group.ActuatorType);
            var count = group.FixedCount;
            if (group.CountParam.Length > 0 && lookup(group.CountParam) is { } countValue)
            {
                count = (int)countValue;
            }

            // Actuator-type fields (e.g. slew rate) are indexed across all groups of that type; the group's own per-item
            // fields by the item index.
            var typeParams = actuatorType?.PerItemParameters ?? [];
            var columns = typeParams.Select(p => new GeometryColumn(p.Label, p.Advanced, p.Identifier, false))
                .Concat(group.PerItemParameters.Select(p => new GeometryColumn(p.Label, p.Advanced, p.Identifier, p.Role == "type")))
                .ToList();
            var rule = metadata.Rules.FirstOrDefault(r => group.PerItemParameters.Any(p => p.Identifier.Length > 0 && p.Identifier == r.SelectIdentifier));

            var firstTypeIndex = typeIndex.GetValueOrDefault(group.ActuatorType);
            var items = new List<GeometryItem>();
            for (var i = 0; i < Math.Max(0, count); i++)
            {
                var actuatorTypeIndex = firstTypeIndex + i;
                var function = actuatorType?.FunctionMin is { } min ? min + actuatorTypeIndex : 0;
                var label = ItemLabel(group, i, metadata.Functions.GetValueOrDefault(function)?.Label ?? string.Empty);
                var cells = typeParams.Select(p => Cell(p, actuatorTypeIndex, lookup))
                    .Concat(group.PerItemParameters.Select(p => Cell(p, i, lookup)))
                    .ToList();
                items.Add(new GeometryItem(label, function, i, ApplyRule(rule, columns, cells, lookup)));
            }

            typeIndex[group.ActuatorType] = firstTypeIndex + Math.Max(0, count);
            groups.Add(new ActuatorGroup(
                group.GroupLabel,
                group.ActuatorType,
                group.Required,
                group.CountParam.Length > 0 && lookup(group.CountParam) is not null ? new ActuatorField(group.CountParam, group.GroupLabel, false) : null,
                columns,
                items,
                [.. group.Parameters.Where(p => lookup(p.Name) is not null).Select(p => new ActuatorField(p.Name, p.Label, p.Advanced))],
                rule));
        }

        return groups;
    }

    /// <summary>Hides, disables and bounds the rule's fields according to the item's select value.</summary>
    private static List<ActuatorField?> ApplyRule(MixerRuleInfo? rule, List<GeometryColumn> columns, List<ActuatorField?> cells, ParameterReader lookup)
    {
        if (rule is null)
        {
            return cells;
        }

        var selectColumn = columns.FindIndex(c => c.Identifier == rule.SelectIdentifier);
        if (selectColumn < 0 || cells[selectColumn] is not { } selectCell || Whole(lookup(selectCell.Param)) is not { } selectValue
            || !rule.Items.TryGetValue((int)selectValue, out var ruleItems))
        {
            return cells;
        }

        for (var column = 0; column < columns.Count; column++)
        {
            var applyIndex = IndexOf(rule.ApplyIdentifiers, columns[column].Identifier);
            if (applyIndex < 0 || applyIndex >= ruleItems.Count || cells[column] is not { } cell)
            {
                continue;
            }

            var ruleItem = ruleItems[applyIndex];
            cells[column] = cell with { Visible = !ruleItem.Hidden, Enabled = !ruleItem.Disabled, Min = ruleItem.Min, Max = ruleItem.Max };
        }

        return cells;
    }

    private static List<OutputGroup> BuildOutputs(ActuatorMetadata metadata, ParameterReader lookup)
    {
        var groups = new List<OutputGroup>();
        foreach (var output in metadata.Outputs)
        {
            var subgroups = output.Subgroups.Select(s =>
            {
                var columns = s.PerChannelParameters.Where(p => ActuatorCondition.Evaluate(p.ShowIf, lookup)).ToList();
                var channels = s.Channels.Select(c =>
                {
                    var cells = columns.Select(p => Cell(p, c.ParamIndex, lookup)).ToList();
                    var functionColumn = columns.FindIndex(p => p.Role == "function");
                    var function = functionColumn >= 0 && cells[functionColumn] is { } f && lookup(f.Param) is { } value ? (int)value : 0;
                    return new OutputChannel(c.Label, c.ParamIndex, function, cells);
                }).Where(c => c.Cells.Any(cell => cell is not null)).ToList();
                return new OutputSubgroup(
                    s.Label,
                    [.. s.Parameters.Where(p => lookup(p.Name) is not null).Select(p => new ActuatorField(p.Name, p.Label, p.Advanced))],
                    [.. columns.Select(p => new OutputChannelColumn(p.Label.Replace('\n', ' '), p.Advanced, p.Role))],
                    channels);
            }).ToList();

            var parameters = output.Parameters.Where(p => lookup(p.Name) is not null).Select(p => new ActuatorField(p.Name, p.Label, p.Advanced)).ToList();
            var hasEnable = output.Parameters.Any(p => p.Role == "enable" && lookup(p.Name) is not null);
            var hasFunctionParams = subgroups.Any(s => s.Channels.Any(c => FunctionCell(s, c) is not null));

            // Drops groups with neither an enable parameter nor any existing function parameter.
            if (!hasEnable && !hasFunctionParams)
            {
                continue;
            }

            groups.Add(new OutputGroup(output.Label, parameters, ActuatorCondition.Evaluate(output.ShowSubgroupsIf, lookup), subgroups));
        }

        return groups;
    }

    /// <summary>The label for a tested function: the surface type when the geometry has one (with the function name added when two surfaces share a type), else the function label.</summary>
    private static string SpecificLabel(int function, IReadOnlyList<ActuatorGroup> geometry, string? functionLabel, ParameterReader lookup, Func<string, long, string?> enumText)
    {
        var fallback = functionLabel ?? function.ToString(CultureInfo.InvariantCulture);
        foreach (var group in geometry)
        {
            var typeColumn = group.Columns.ToList().FindIndex(c => c.IsType);
            if (typeColumn < 0 || group.Items.FirstOrDefault(i => i.Function == function) is not { } item
                || item.Cells[typeColumn] is not { } typeCell || Whole(lookup(typeCell.Param)) is not { } typeValue)
            {
                continue;
            }

            var typeText = enumText(typeCell.Param, typeValue) ?? typeValue.ToString(CultureInfo.InvariantCulture);
            var shared = group.Items.Any(other => other.Function != function && other.Cells[typeColumn] is { } otherCell
                && Whole(lookup(otherCell.Param)) == typeValue);
            return shared ? $"{typeText} ({fallback})" : typeText;
        }

        return fallback;
    }

    private static bool IsSurface(int function, IReadOnlyList<ActuatorGroup> geometry) =>
        geometry.Any(g => g.ActuatorType == SurfaceType && g.Items.Any(i => i.Function == function));

    private static string ItemLabel(MixerGroupInfo group, int index, string functionLabel)
    {
        var prefix = group.ItemLabelPrefix.Count switch
        {
            1 => group.ItemLabelPrefix[0].Replace("${i}", (index + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            > 1 when index < group.ItemLabelPrefix.Count => group.ItemLabelPrefix[index],
            _ => string.Empty
        };
        return prefix.Length > 0 ? $"{prefix} ({functionLabel})" : functionLabel;
    }

    /// <summary>Resolves <c>${i}</c> (plus the field's index offset) and checks the vehicle has it; bitset fields address bit <c>index + offset</c>.</summary>
    private static ActuatorField? Cell(ActuatorParamInfo parameter, int index, ParameterReader lookup)
    {
        if (parameter.Name.Length == 0)
        {
            return null;
        }

        var used = index + parameter.IndexOffset;
        var name = parameter.Name.Replace("${i}", used.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return lookup(name) is null ? null
            : new ActuatorField(name, parameter.Label, parameter.Advanced, parameter.ShowAs == ParamShowAs.Bitset ? used : null, parameter.ShowAs == ParamShowAs.TrueIfPositive);
    }

    private static long? Whole(double? value) => value is { } v && v == Math.Round(v) ? (long)v : null;

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        if (value.Length == 0)
        {
            return -1;
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}
