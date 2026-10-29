using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ketras.Mavlink.Protocols;
using Ketras.Notifications;

namespace Ketras.Events;

/// <summary>One enum of the events metadata.</summary>
/// <param name="BaseType">The integer type it is sent as.</param>
/// <param name="Entries">Value → (name, description).</param>
/// <param name="IsBitfield">Each bit is an entry.</param>
/// <param name="Separator">Between the bits of a bitfield when shown.</param>
public sealed record EventEnum(string BaseType, IReadOnlyDictionary<ulong, (string Name, string Description)> Entries, bool IsBitfield, string Separator);

/// <summary>One argument of an event as received.</summary>
/// <param name="Raw">The integer value (a float's bits are not used).</param>
/// <param name="Number">The value as a number.</param>
/// <param name="Enum">The enum it belongs to; null for a plain number.</param>
public sealed record EventValue(ulong Raw, double Number, EventEnum? Enum);

/// <summary>An event interpreted with its definition.</summary>
/// <param name="Id">Event id.</param>
/// <param name="Name">Its name in the metadata, e.g. "commander_arming_check_summary".</param>
/// <param name="Group">Its group: "default", "arming_check", "health", "calibration".</param>
/// <param name="Type">Its type: "", "summary", "append_health_and_arming_messages".</param>
/// <param name="Message">The message with its arguments filled in.</param>
/// <param name="Description">The longer description, filled in the same way; empty when none.</param>
/// <param name="LogLevel">The external log level (0 Emergency … 7 Debug, 8 Protocol).</param>
/// <param name="Values">The arguments, in order.</param>
public sealed record ParsedEvent(uint Id, string Name, string Group, string Type, string Message, string Description, int LogLevel, IReadOnlyList<EventValue> Values)
{
    /// <summary>For the operator: Emergency to Error are errors, Warning is a warning, Notice and below are info.</summary>
    public NotificationSeverity Severity => LogLevel switch
    {
        <= 3 => NotificationSeverity.Error,
        4 => NotificationSeverity.Warning,
        _ => NotificationSeverity.Info
    };

    /// <summary>Protocol events (summaries and the like) are machinery, not for display.</summary>
    public bool IsForDisplay => LogLevel <= 7;
}

/// <summary>
/// The vehicle's events metadata (libevents JSON, COMP_METADATA_TYPE_EVENTS): event definitions, enums and the flight
/// modes of each navigation mode group. Interprets received events as libevents' parser does
/// (libs/python/libevents_parse/parser.py): arguments packed little-endian in definition order; messages with
/// <c>{N[:.D][unit]}</c> placeholders, <c>&lt;param&gt;</c>, <c>&lt;a&gt;</c> and <c>&lt;profile&gt;</c> tags and backslash escapes.
/// Immutable; built by <see cref="EventsParser"/>.
/// </summary>
public sealed partial class EventDefinitions
{
    private const string Profile = "normal";

    private static readonly Dictionary<string, int> Sizes = new(StringComparer.Ordinal)
    {
        ["uint8_t"] = 1, ["int8_t"] = 1, ["uint16_t"] = 2, ["int16_t"] = 2, ["uint32_t"] = 4, ["int32_t"] = 4,
        ["uint64_t"] = 8, ["int64_t"] = 8, ["float"] = 4
    };

    private readonly IReadOnlyDictionary<uint, Definition> _events;
    private readonly IReadOnlyDictionary<string, EventEnum> _enums;
    private readonly IReadOnlyDictionary<uint, int> _modeGroups;

    internal EventDefinitions(IReadOnlyDictionary<uint, Definition> events, IReadOnlyDictionary<string, EventEnum> enums, IReadOnlyDictionary<uint, int> modeGroups)
    {
        _events = events;
        _enums = enums;
        _modeGroups = modeGroups;
    }

    /// <summary>Number of event definitions.</summary>
    public int Count => _events.Count;

    /// <summary>The event with its definition; null when the metadata does not know it.</summary>
    public ParsedEvent? Interpret(VehicleEvent @event)
    {
        if (!_events.TryGetValue(@event.Id, out var definition))
        {
            return null;
        }

        var values = new List<EventValue>();
        var offset = 0;
        foreach (var type in definition.ArgumentTypes)
        {
            var @enum = type.Contains("::", StringComparison.Ordinal) ? _enums.GetValueOrDefault(type)
                : Sizes.ContainsKey(type) ? null : _enums.GetValueOrDefault(definition.Namespace + "::" + type);
            var baseType = @enum?.BaseType ?? type;
            if (!Sizes.TryGetValue(baseType, out var size) || offset + size > @event.Arguments.Length)
            {
                return null;
            }

            values.Add(Read(baseType, @event.Arguments.AsSpan(offset, size), @enum));
            offset += size;
        }

        return new ParsedEvent(@event.Id, definition.Name, definition.Group, definition.Type, Render(definition.Message, values),
            Render(definition.Description, values), @event.ExternalLogLevel, values);
    }

    /// <summary>The navigation mode group (its bit index) of a PX4 custom mode; null when the metadata has none.</summary>
    public int? ModeGroupOf(uint customMode) => _modeGroups.TryGetValue(customMode, out var group) ? group : null;

    /// <summary>The text an enum value or bitfield shows.</summary>
    public static string Display(EventValue value)
    {
        if (value.Enum is not { } @enum)
        {
            return value.Number.ToString(CultureInfo.InvariantCulture);
        }

        if (!@enum.IsBitfield)
        {
            return @enum.Entries.TryGetValue(value.Raw, out var entry) ? entry.Description : $"(unknown: {value.Raw})";
        }

        var bits = new List<string>();
        for (var bit = 0; bit < 64; bit++)
        {
            var mask = 1UL << bit;
            if ((value.Raw & mask) != 0)
            {
                bits.Add(@enum.Entries.TryGetValue(mask, out var entry) ? entry.Description : $"(unknown: {value.Raw})");
            }
        }

        return string.Join(@enum.Separator, bits);
    }

    private static EventValue Read(string type, ReadOnlySpan<byte> bytes, EventEnum? @enum)
    {
        (ulong raw, double number) = type switch
        {
            "uint8_t" => (bytes[0], bytes[0]),
            "int8_t" => ((ulong)(sbyte)bytes[0], (sbyte)bytes[0]),
            "uint16_t" => (BinaryPrimitives.ReadUInt16LittleEndian(bytes), BinaryPrimitives.ReadUInt16LittleEndian(bytes)),
            "int16_t" => ((ulong)BinaryPrimitives.ReadInt16LittleEndian(bytes), BinaryPrimitives.ReadInt16LittleEndian(bytes)),
            "uint32_t" => (BinaryPrimitives.ReadUInt32LittleEndian(bytes), BinaryPrimitives.ReadUInt32LittleEndian(bytes)),
            "int32_t" => ((ulong)BinaryPrimitives.ReadInt32LittleEndian(bytes), BinaryPrimitives.ReadInt32LittleEndian(bytes)),
            "uint64_t" => (BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes)),
            "int64_t" => ((ulong)BinaryPrimitives.ReadInt64LittleEndian(bytes), BinaryPrimitives.ReadInt64LittleEndian(bytes)),
            _ => (0UL, BinaryPrimitives.ReadSingleLittleEndian(bytes))
        };
        return new EventValue(raw, number, @enum);
    }

    /// <summary>Fills in a message template; tags become their text, unknown tags and other profiles' text go.</summary>
    private static string Render(string template, IReadOnlyList<EventValue> values)
    {
        var text = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '\\' && i + 1 < template.Length)
            {
                text.Append(template[i + 1]);
                i += 2;
                continue;
            }

            if (c == '<' && Tag().Match(template, i) is { Success: true } tag)
            {
                var name = tag.Groups[1].Value;
                var contentStart = tag.Index + tag.Length;
                var end = template.IndexOf("</" + name + ">", contentStart, StringComparison.Ordinal);
                if (end >= 0)
                {
                    var content = template[contentStart..end];
                    text.Append(name switch
                    {
                        "profile" => tag.Groups[3].Value is var profile && (profile.StartsWith('!') ? profile[1..] != Profile : profile == Profile)
                            ? Render(content, values) : string.Empty,
                        "a" or "param" => content,
                        _ => string.Empty
                    });
                    i = end + name.Length + 3;
                    continue;
                }
            }

            if (c == '{' && Placeholder().Match(template, i) is { Success: true } placeholder)
            {
                var index = int.Parse(placeholder.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
                if (index >= 0 && index < values.Count)
                {
                    var value = values[index];
                    text.Append(placeholder.Groups[2].Success && value.Enum is null
                        ? value.Number.ToString("F" + placeholder.Groups[2].Value, CultureInfo.InvariantCulture)
                        : Display(value));
                    if (placeholder.Groups[3].Success)
                    {
                        text.Append(' ').Append(placeholder.Groups[3].Value == "m_v" ? "m" : placeholder.Groups[3].Value);
                    }

                    i = placeholder.Index + placeholder.Length;
                    continue;
                }
            }

            text.Append(c);
            i++;
        }

        return text.ToString().Trim();
    }

    [GeneratedRegex("""\G<([a-z]+)(?: ([a-z]+)="([^"]*)")?>""")]
    private static partial Regex Tag();

    [GeneratedRegex("""\G\{(\d+)(?::(?:\.(\d+))?)?(m|m_v|m/s|m\^2|C)?\}""")]
    private static partial Regex Placeholder();

    /// <summary>One event definition.</summary>
    internal sealed record Definition(string Name, string Namespace, string Group, string Type, string Message, string Description, IReadOnlyList<string> ArgumentTypes);
}
