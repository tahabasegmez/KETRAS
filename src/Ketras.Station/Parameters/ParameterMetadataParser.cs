using System.Text.Json;
using Ketras.Mavlink.Messages;

namespace Ketras.Station.Parameters;

/// <summary>Parses PX4's parameter metadata (<c>parameters.json</c>, component metadata type 1) by parameter name.</summary>
public static class ParameterMetadataParser
{
    /// <summary>MAVLink's longest parameter name.</summary>
    public const int MaxNameLength = 16;

    /// <summary>Every parameter described; entries with an unknown type or name are left out.</summary>
    /// <exception cref="FormatException">The text is not JSON or has no <c>parameters</c> array.</exception>
    public static IReadOnlyDictionary<string, ParameterMetadata> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("parameters", out var parameters)
                || parameters.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Not PX4 parameter metadata: no 'parameters' array.");
            }

            var result = new Dictionary<string, ParameterMetadata>(StringComparer.Ordinal);
            foreach (var element in parameters.EnumerateArray())
            {
                if (TryParse(element) is { } metadata)
                {
                    result[metadata.Name] = metadata;
                }
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new FormatException("Parameter metadata is not valid JSON.", exception);
        }
    }

    private static ParameterMetadata? TryParse(JsonElement element)
    {
        var name = String(element, "name");
        if (name.Length is 0 or > MaxNameLength || Type(String(element, "type")) is not { } type)
        {
            return null;
        }

        return new ParameterMetadata
        {
            Name = name,
            Type = type,
            Group = String(element, "group"),
            Category = String(element, "category"),
            ShortDescription = String(element, "shortDesc"),
            LongDescription = String(element, "longDesc").Trim(),
            Units = String(element, "units"),
            Min = Number(element, "min"),
            Max = Number(element, "max"),
            Increment = Number(element, "increment"),
            DecimalPlaces = Number(element, "decimalPlaces") is { } places ? (int)places : null,
            Default = Number(element, "default"),
            Options = Options(element),
            Bits = Bits(element),
            RebootRequired = Flag(element, "rebootRequired"),
            Volatile = Flag(element, "volatile")
        };
    }

    private static MavParamType? Type(string type) => type switch
    {
        "Int32" => MavParamType.Int32,
        "Float" => MavParamType.Real32,
        "Int8" => MavParamType.Int8,
        "UInt8" => MavParamType.Uint8,
        "Int16" => MavParamType.Int16,
        "UInt16" => MavParamType.Uint16,
        "UInt32" => MavParamType.Uint32,
        "Int64" => MavParamType.Int64,
        "UInt64" => MavParamType.Uint64,
        "Double" => MavParamType.Real64,
        _ => null
    };

    private static List<EnumOption> Options(JsonElement element)
    {
        var options = new List<EnumOption>();
        if (element.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in values.EnumerateArray())
            {
                if (Number(entry, "value") is { } value)
                {
                    options.Add(new EnumOption((long)Math.Round(value), String(entry, "description")));
                }
            }
        }

        return options;
    }

    private static List<BitmaskBit> Bits(JsonElement element)
    {
        var bits = new List<BitmaskBit>();
        if (element.TryGetProperty("bitmask", out var bitmask) && bitmask.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in bitmask.EnumerateArray())
            {
                if (Number(entry, "index") is { } index and >= 0 and < 64)
                {
                    bits.Add(new BitmaskBit((int)index, String(entry, "description")));
                }
            }
        }

        return bits;
    }

    private static string String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static double? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    private static bool Flag(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
