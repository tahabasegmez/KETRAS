using System.Globalization;
using System.Text;
using Ketras.App.Resources;
using Ketras.Mavlink.Messages;
using Ketras.Station.Parameters;

namespace Ketras.App.Setup;

/// <summary>Parameter values as the operator reads and types them: enum names, the metadata's decimals and unit, and the range; the current culture's numbers.</summary>
public static class ParameterText
{
    /// <summary>The value as shown: an enum's name, else the number with the unit.</summary>
    public static string Format(double value, MavParamType type, ParameterMetadata? metadata, bool withUnit = true)
    {
        if (metadata is { IsEnum: true } && metadata.Options.FirstOrDefault(o => o.Value == Math.Round(value)) is { } option)
        {
            return option.Description;
        }

        var number = Number(value, type, metadata);
        return withUnit && metadata is { Units.Length: > 0 } ? $"{number} {metadata.Units}" : number;
    }

    /// <summary>The number alone, in the parameter's precision.</summary>
    public static string Number(double value, MavParamType type, ParameterMetadata? metadata) =>
        ParameterRules.IsInteger(type) ? Math.Round(value).ToString(CultureInfo.CurrentCulture)
        : metadata?.DecimalPlaces is { } places ? value.ToString($"F{places}", CultureInfo.CurrentCulture)
        : (type == MavParamType.Real32 ? (double)(float)value : value).ToString("0.######", CultureInfo.CurrentCulture);

    /// <summary>Parses the operator's text and checks it; the problem text is empty when it is fine.</summary>
    public static bool TryParse(string? text, MavParamType type, ParameterMetadata? metadata, out double value, out string problem)
    {
        problem = string.Empty;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) || !double.IsFinite(value))
        {
            problem = Strings.FieldNotNumber;
            return false;
        }

        switch (ParameterRules.Check(value, type, metadata))
        {
            case ValueProblem.NotWhole:
                problem = Strings.ParamNotWhole;
                return false;
            case ValueProblem.BelowMinimum or ValueProblem.AboveMaximum:
                problem = string.Format(CultureInfo.CurrentCulture, Strings.ParamOutOfRange, Range(metadata));
                return false;
            default:
                return true;
        }
    }

    /// <summary>"0.5 … 40 m/s", or "any value".</summary>
    public static string Range(ParameterMetadata? metadata)
    {
        if (metadata?.Min is null && metadata?.Max is null)
        {
            return Strings.ParamAnyValue;
        }

        var min = metadata.Min is { } lo ? lo.ToString("0.######", CultureInfo.CurrentCulture) : "-∞";
        var max = metadata.Max is { } hi ? hi.ToString("0.######", CultureInfo.CurrentCulture) : "∞";
        return metadata.Units.Length > 0 ? $"{min} … {max} {metadata.Units}" : $"{min} … {max}";
    }

    /// <summary>The tooltip: name, descriptions, range, default, bits and reboot note.</summary>
    public static string Describe(string name, MavParamType type, ParameterMetadata? metadata)
    {
        if (metadata is null)
        {
            return $"{name}\n{Strings.ParamNoMetadata}";
        }

        var text = new StringBuilder(name);
        if (metadata.ShortDescription.Length > 0)
        {
            text.Append('\n').Append(metadata.ShortDescription);
        }

        if (metadata.LongDescription.Length > 0)
        {
            text.Append("\n\n").Append(metadata.LongDescription);
        }

        text.Append("\n\n").Append(string.Format(CultureInfo.CurrentCulture, Strings.ParamRange, Range(metadata)));
        if (metadata.Default is { } defaultValue)
        {
            text.Append("   ").Append(string.Format(CultureInfo.CurrentCulture, Strings.ParamDefault, Format(defaultValue, type, metadata)));
        }

        foreach (var bit in metadata.Bits)
        {
            text.Append('\n').Append(CultureInfo.CurrentCulture, $"bit {bit.Index} ({1L << bit.Index}): {bit.Description}");
        }

        if (metadata.RebootRequired)
        {
            text.Append('\n').Append(Strings.ParamRebootRequired);
        }

        return text.ToString();
    }
}
