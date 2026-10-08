using System.Globalization;
using System.Text.RegularExpressions;
using Ketras.Station.Parameters;

namespace Ketras.Station.Setup;

/// <summary>The condition strings of <c>actuators.json</c> (<c>option</c>, <c>show-subgroups-if</c>,
/// <c>show-if</c>, notes): <c>true | false | PARAM&lt;op&gt;integer</c>, op one of <c>&gt; &gt;= == != &lt; &lt;=</c>.
/// Empty or unrecognised text is true; a missing or non-integer parameter makes it false.</summary>
public static partial class ActuatorCondition
{
    /// <summary>Whether <paramref name="condition"/> holds for the vehicle's values.</summary>
    public static bool Evaluate(string condition, ParameterReader lookup)
    {
        if (condition.Length == 0 || condition == "true")
        {
            return true;
        }

        if (condition == "false")
        {
            return false;
        }

        var match = Pattern().Match(condition);
        if (!match.Success || !int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var operand))
        {
            return true;
        }

        var op = match.Groups[2].Value;
        if (op is not (">" or ">=" or "==" or "!=" or "<" or "<="))
        {
            return true;
        }

        if (lookup(match.Groups[1].Value) is not { } number || number != Math.Round(number))
        {
            return false;
        }

        var value = (long)number;
        return op switch
        {
            ">" => value > operand,
            ">=" => value >= operand,
            "==" => value == operand,
            "!=" => value != operand,
            "<" => value < operand,
            _ => value <= operand
        };
    }

    [GeneratedRegex(@"^([0-9A-Za-z_-]+)([\!=<>]+)(-?\d+)$")]
    private static partial Regex Pattern();
}
