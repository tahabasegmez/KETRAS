using System.Globalization;
using System.Text.Json;

namespace Ketras.Setup;

/// <summary>Parses PX4's <c>actuators.json</c> (version 1) into <see cref="ActuatorMetadata"/></summary>
public static class ActuatorMetadataParser
{
    /// <summary>The metadata in <paramref name="json"/>.</summary>
    /// <exception cref="FormatException">Not version-1 actuator metadata.</exception>
    public static ActuatorMetadata Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("functions_v1", out var functions) || functions.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("mixer_v1", out var mixer) || mixer.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("outputs_v1", out var outputs) || outputs.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Not PX4 actuator metadata: 'functions_v1', 'mixer_v1' or 'outputs_v1' missing.");
            }

            if (GetDouble(root, "version") is { } version && version != 1)
            {
                throw new FormatException($"Unsupported actuator metadata version {version.ToString(CultureInfo.InvariantCulture)} (expected 1).");
            }

            return new ActuatorMetadata
            {
                Functions = ParseFunctions(functions),
                ActuatorTypes = ParseActuatorTypes(mixer),
                MixerConfigs = Objects(mixer, "config").Select(ParseMixerConfig).ToList(),
                Rules = Objects(mixer, "rules").Select(ParseRule).ToList(),
                Outputs = outputs.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object).Select(ParseOutputGroup).ToList(),
                ShowUiIf = GetString(root, "show-ui-if")
            };
        }
        catch (JsonException exception)
        {
            throw new FormatException("Actuator metadata is not valid JSON.", exception);
        }
    }

    private static Dictionary<int, OutputFunctionInfo> ParseFunctions(JsonElement functions)
    {
        var result = new Dictionary<int, OutputFunctionInfo>();
        foreach (var property in functions.EnumerateObject())
        {
            if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var note = property.Value.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.Object ? n : default;
            result[id] = new OutputFunctionInfo(
                id,
                GetString(property.Value, "label"),
                GetBool(property.Value, "exclude-from-actuator-testing"),
                note.ValueKind == JsonValueKind.Object ? GetString(note, "condition") : string.Empty,
                note.ValueKind == JsonValueKind.Object ? GetString(note, "text") : string.Empty);
        }

        return result;
    }

    private static Dictionary<string, ActuatorTypeInfo> ParseActuatorTypes(JsonElement mixer)
    {
        var result = new Dictionary<string, ActuatorTypeInfo>(StringComparer.Ordinal);
        if (!mixer.TryGetProperty("actuator-types", out var types) || types.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in types.EnumerateObject())
        {
            var type = property.Value;
            var values = type.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
            result[property.Name] = new ActuatorTypeInfo(
                property.Name,
                GetDouble(type, "function-min") is { } min ? (int)min : null,
                GetDouble(type, "function-max") is { } max ? (int)max : null,
                values.ValueKind == JsonValueKind.Object ? GetDouble(values, "min") ?? -1 : -1,
                values.ValueKind == JsonValueKind.Object ? GetDouble(values, "max") ?? 1 : 1,
                values.ValueKind == JsonValueKind.Object ? GetDouble(values, "default") ?? 0 : 0,
                values.ValueKind == JsonValueKind.Object && GetBool(values, "default-is-nan"),
                (int)(GetDouble(type, "label-index-offset") ?? 0),
                Objects(type, "per-item-parameters").Select(ParseParam).ToList());
        }

        return result;
    }

    private static MixerConfigInfo ParseMixerConfig(JsonElement config) => new(
        GetString(config, "option"),
        GetString(config, "title"),
        GetString(config, "help-url"),
        Objects(config, "actuators").Select(ParseMixerGroup).ToList());

    private static MixerGroupInfo ParseMixerGroup(JsonElement group)
    {
        var countParam = string.Empty;
        var fixedCount = 0;
        if (group.TryGetProperty("count", out var count))
        {
            if (count.ValueKind == JsonValueKind.String)
            {
                countParam = count.GetString() ?? string.Empty;
            }
            else if (count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var number))
            {
                fixedCount = number;
            }
        }

        var prefixes = new List<string>();
        if (group.TryGetProperty("item-label-prefix", out var prefix))
        {
            if (prefix.ValueKind == JsonValueKind.String)
            {
                prefixes.Add(prefix.GetString() ?? string.Empty);
            }
            else if (prefix.ValueKind == JsonValueKind.Array)
            {
                prefixes.AddRange(prefix.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty));
            }
        }

        return new MixerGroupInfo(
            GetString(group, "actuator-type"),
            countParam,
            fixedCount,
            GetString(group, "group-label"),
            GetBool(group, "required"),
            prefixes,
            Objects(group, "parameters").Select(ParseParam).ToList(),
            Objects(group, "per-item-parameters").Select(ParseParam).ToList());
    }

    private static MixerRuleInfo ParseRule(JsonElement rule)
    {
        var items = new Dictionary<int, IReadOnlyList<RuleItemInfo>>();
        if (rule.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in itemsElement.EnumerateObject())
            {
                if (int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectValue) && property.Value.ValueKind == JsonValueKind.Array)
                {
                    items[selectValue] = property.Value.EnumerateArray()
                        .Select(i => i.ValueKind == JsonValueKind.Object
                            ? new RuleItemInfo(GetDouble(i, "default"), GetDouble(i, "min"), GetDouble(i, "max"), GetBool(i, "hidden"), GetBool(i, "disabled"))
                            : new RuleItemInfo(null, null, null, false, false))
                        .ToList();
                }
            }
        }

        return new MixerRuleInfo(
            GetString(rule, "select-identifier"),
            Strings(rule, "apply-identifiers"),
            items);
    }

    private static OutputGroupInfo ParseOutputGroup(JsonElement output) => new(
        GetString(output, "label"),
        GetString(output, "show-subgroups-if"),
        Objects(output, "parameters").Select(ParseParam).ToList(),
        Objects(output, "subgroups").Select(s => new OutputSubgroupInfo(
            GetString(s, "label"),
            Objects(s, "parameters").Select(ParseParam).ToList(),
            Objects(s, "per-channel-parameters").Select(ParseParam).ToList(),
            Objects(s, "channels").Select(c => new OutputChannelInfo(GetString(c, "label"), (int)(GetDouble(c, "param-index") ?? 0))).ToList())).ToList());

    private static ActuatorParamInfo ParseParam(JsonElement parameter) => new(
        GetString(parameter, "name"),
        GetString(parameter, "label"),
        GetBool(parameter, "advanced"),
        GetString(parameter, "function"),
        GetString(parameter, "identifier"),
        (int)(GetDouble(parameter, "index-offset") ?? 0),
        GetString(parameter, "show-as") switch
        {
            "bitset" => ParamShowAs.Bitset,
            "true-if-positive" => ParamShowAs.TrueIfPositive,
            _ => ParamShowAs.Value
        },
        GetString(parameter, "show-if"),
        parameter.TryGetProperty("value", out var fixedValues) && fixedValues.ValueKind == JsonValueKind.Array
            ? fixedValues.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetDouble()).ToList()
            : []);

    private static IEnumerable<JsonElement> Objects(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList()
            : [];

    private static List<string> Strings(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty).ToList()
            : [];

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double? GetDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private static bool GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
