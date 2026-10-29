using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Protocols;

namespace Ketras.Parameters;

/// <summary>One line of a parameter file.</summary>
/// <param name="VehicleId">The system id it was saved from; null in a two-column file.</param>
/// <param name="ComponentId">The component id; null in a two-column file.</param>
/// <param name="Name">The parameter.</param>
/// <param name="Value">The value; null when the text is not a number.</param>
/// <param name="Text">The value as written.</param>
public sealed record ParamsFileEntry(int? VehicleId, int? ComponentId, string Name, double? Value, string Text);

/// <summary>How a file value relates to the vehicle.</summary>
public enum ParamsDiffKind
{
    /// <summary>It differs from the vehicle's value.</summary>
    Changed,

    /// <summary>The vehicle does not report the parameter.</summary>
    NotOnVehicle,

    /// <summary>Not a valid value for the parameter.</summary>
    Invalid
}

/// <summary>One difference between a file and the vehicle.</summary>
/// <param name="Name">The parameter.</param>
/// <param name="Kind">The difference.</param>
/// <param name="FileValue">The file's value (null when invalid).</param>
/// <param name="VehicleValue">The vehicle's value (null when not on the vehicle).</param>
/// <param name="Text">The file's text.</param>
public sealed record ParamsDiffRow(string Name, ParamsDiffKind Kind, double? FileValue, double? VehicleValue, string Text);

/// <summary>A file compared with the vehicle.</summary>
/// <param name="Rows">The differences, changes first.</param>
/// <param name="ParsedCount">Lines read.</param>
/// <param name="UnchangedCount">Lines equal to the vehicle.</param>
/// <param name="OtherVehicle">It was saved from another system id.</param>
public sealed record ParamsDiff(IReadOnlyList<ParamsDiffRow> Rows, int ParsedCount, int UnchangedCount, bool OtherVehicle);

/// <summary>
/// <c>.params</c> files: tab-separated <c>VehicleId ComponentId Name Value MAV_PARAM_TYPE</c> under
/// <c>#</c> header lines. Reading also accepts the two-column <c>Name,Value</c> form. Numbers are invariant.
/// </summary>
public static partial class ParamsFileFormat
{
    /// <summary>Writes every value, sorted by name.</summary>
    public static string Write(IReadOnlyDictionary<string, ParameterEntry> values, int systemId, int componentId, string firmware)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"# Onboard parameters for Vehicle {systemId}\n");
        text.Append("#\n");
        text.Append("# Stack: PX4 Pro\n");
        text.Append(CultureInfo.InvariantCulture, $"# Version: {firmware}\n");
        text.Append("#\n");
        text.Append("# Vehicle-Id Component-Id Name Value Type\n");
        foreach (var name in values.Keys.Order(StringComparer.Ordinal))
        {
            var value = values[name].Value;
            var number = ParameterRules.IsInteger(value.Type)
                ? Math.Round(value.Value).ToString(CultureInfo.InvariantCulture)
                : value.Type == MavParamType.Real32 ? ((float)value.Value).ToString("R", CultureInfo.InvariantCulture) : value.Value.ToString("R", CultureInfo.InvariantCulture);
            text.Append(CultureInfo.InvariantCulture, $"{systemId}\t{componentId}\t{name}\t{number}\t{(uint)value.Type}\n");
        }

        return text.ToString();
    }

    /// <summary>The lines of a file; lines that are neither format are skipped.</summary>
    public static IReadOnlyList<ParamsFileEntry> Read(string text)
    {
        var entries = new List<ParamsFileEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var fields = Separator().Split(line);
            if (fields.Length == 5
                && int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vehicleId)
                && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var componentId))
            {
                entries.Add(new ParamsFileEntry(vehicleId, componentId, fields[2], Number(fields[3]), fields[3]));
            }
            else if (fields.Length == 2)
            {
                entries.Add(new ParamsFileEntry(null, null, fields[0], Number(fields[1]), fields[1]));
            }
        }

        return entries;
    }

    /// <summary>Compares a file with the vehicle's values; only parameters the vehicle reports can be written, in its own type.</summary>
    public static ParamsDiff Compare(IReadOnlyList<ParamsFileEntry> entries, IReadOnlyDictionary<string, ParameterEntry> vehicle,
        Func<string, ParameterMetadata?> metadata, int systemId)
    {
        var rows = new List<ParamsDiffRow>();
        var unchanged = 0;
        foreach (var entry in entries)
        {
            if (!vehicle.TryGetValue(entry.Name, out var current))
            {
                rows.Add(new ParamsDiffRow(entry.Name, ParamsDiffKind.NotOnVehicle, entry.Value, null, entry.Text));
            }
            else if (entry.Value is not { } value || ParameterRules.Check(value, current.Value.Type, metadata(entry.Name)) != ValueProblem.None)
            {
                rows.Add(new ParamsDiffRow(entry.Name, ParamsDiffKind.Invalid, null, current.Value.Value, entry.Text));
            }
            else if (ParameterRules.Same(value, current.Value.Value, current.Value.Type))
            {
                unchanged++;
            }
            else
            {
                rows.Add(new ParamsDiffRow(entry.Name, ParamsDiffKind.Changed, value, current.Value.Value, entry.Text));
            }
        }

        return new ParamsDiff([.. rows.OrderBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.Ordinal)], entries.Count, unchanged,
            entries.Any(e => e.VehicleId is { } id && id != systemId));
    }

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    [GeneratedRegex(@"[\t ,]+")]
    private static partial Regex Separator();
}
