using System.Globalization;
using System.Text.Json.Nodes;

namespace Ketras.Rosbridge;

/// <summary>
/// One message layout as rosapi reports it (<c>rosapi_msgs/msg/TypeDef</c>): field names, field
/// types and array lengths (-1 = not an array).
/// </summary>
/// <param name="Type">Type name, e.g. <c>std_msgs/String</c>.</param>
/// <param name="FieldNames">Field names in declaration order.</param>
/// <param name="FieldTypes">Primitive name (<c>bool</c>, <c>int32</c>, <c>float64</c>, <c>string</c>, …) or nested type name.</param>
/// <param name="ArrayLengths">-1 for a scalar, 0 for a sequence, N for a fixed array.</param>
public sealed record RosTypeDef(string Type, IReadOnlyList<string> FieldNames, IReadOnlyList<string> FieldTypes, IReadOnlyList<int> ArrayLengths)
{
    /// <summary>Reads a rosapi <c>typedefs</c> array.</summary>
    public static IReadOnlyList<RosTypeDef> ParseList(JsonNode? typedefs)
    {
        var list = new List<RosTypeDef>();
        if (typedefs is not JsonArray array)
        {
            return list;
        }

        foreach (var item in array)
        {
            if (item is null)
            {
                continue;
            }

            list.Add(new RosTypeDef(
                item["type"]?.GetValue<string>() ?? string.Empty,
                Strings(item["fieldnames"]),
                Strings(item["fieldtypes"]),
                item["fieldarraylen"] is JsonArray lengths ? lengths.Select(n => n?.GetValue<int>() ?? -1).ToList() : []));
        }

        return list;
    }

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(n => n?.GetValue<string>() ?? string.Empty).ToList() : [];
}

/// <summary>
/// Turns text field values into a rosbridge JSON message using the layout from rosapi.
/// rosbridge refuses a JSON string for a bool or number field, so the conversion has to know the
/// real field types.
/// </summary>
public static class RosMessageBuilder
{
    private static readonly HashSet<string> BoolTypes = ["bool", "boolean"];

    private static readonly HashSet<string> IntegerTypes =
        ["byte", "char", "octet", "int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64"];

    private static readonly HashSet<string> FloatTypes = ["float", "float32", "float64", "double"];

    /// <summary>Builds the message. <paramref name="layout"/>[0] is the message itself, the rest are
    /// its nested types. Throws <see cref="FormatException"/> for an unknown field or a bad value.</summary>
    public static JsonObject Build(IReadOnlyList<RosTypeDef> layout, IReadOnlyDictionary<string, string>? values)
    {
        if (layout.Count == 0)
        {
            throw new FormatException("no type layout");
        }

        var message = new JsonObject();
        if (values is null)
        {
            return message;
        }

        foreach (var (key, text) in values)
        {
            var path = key.Split('.');
            var definition = layout[0];
            var target = message;
            for (var i = 0; i < path.Length; i++)
            {
                var index = IndexOf(definition, path[i]);
                if (index < 0)
                {
                    throw new FormatException($"{definition.Type} has no field '{path[i]}'");
                }

                var fieldType = definition.FieldTypes[index];
                var arrayLength = index < definition.ArrayLengths.Count ? definition.ArrayLengths[index] : -1;
                if (arrayLength >= 0)
                {
                    throw new FormatException($"'{key}' is an array field; array values are not supported");
                }

                if (i == path.Length - 1)
                {
                    target[path[i]] = Convert(key, fieldType, text);
                    break;
                }

                definition = layout.FirstOrDefault(d => d.Type == fieldType)
                    ?? throw new FormatException($"'{key}': '{path[i]}' is not a nested message");
                if (target[path[i]] is not JsonObject child)
                {
                    child = new JsonObject();
                    target[path[i]] = child;
                }

                target = child;
            }
        }

        return message;
    }

    private static int IndexOf(RosTypeDef definition, string name)
    {
        for (var i = 0; i < definition.FieldNames.Count && i < definition.FieldTypes.Count; i++)
        {
            if (definition.FieldNames[i] == name)
            {
                return i;
            }
        }

        return -1;
    }

    private static JsonNode Convert(string key, string fieldType, string text)
    {
        var value = text.Trim();
        if (BoolTypes.Contains(fieldType))
        {
            return value.ToLowerInvariant() switch
            {
                "true" or "1" => JsonValue.Create(true),
                "false" or "0" => JsonValue.Create(false),
                _ => throw new FormatException($"'{key}' needs true or false, got '{text}'"),
            };
        }

        if (IntegerTypes.Contains(fieldType))
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                ? JsonValue.Create(integer)
                : throw new FormatException($"'{key}' needs an integer ({fieldType}), got '{text}'");
        }

        if (FloatTypes.Contains(fieldType))
        {
            // JSON has no NaN or infinity, so rosbridge cannot receive them.
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                ? JsonValue.Create(number)
                : throw new FormatException($"'{key}' needs a finite number ({fieldType}), got '{text}'");
        }

        if (fieldType.StartsWith("string", StringComparison.Ordinal) || fieldType.StartsWith("wstring", StringComparison.Ordinal))
        {
            return JsonValue.Create(text);
        }

        throw new FormatException($"'{key}' has type {fieldType}, which cannot be set from text");
    }
}
