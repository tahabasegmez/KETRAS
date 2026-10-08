using System.Globalization;
using System.Text.Json;

namespace Ketras.Station.Events;

/// <summary>
/// Parser: the vehicle's events metadata (libevents JSON, version 1 or 2) to <see cref="EventDefinitions"/>. An event's id
/// is its component (the key under <c>components</c>) in the top 8 bits and its sub-id in the lower 24; enums are
/// named <c>namespace::name</c>; <c>navigation_mode_groups.groups</c> lists the PX4 custom modes of each group bit.
/// </summary>
public static class EventsParser
{
    /// <summary>Parses the metadata; throws <see cref="FormatException"/> when it is not libevents JSON.</summary>
    public static EventDefinitions Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var version) || version.GetInt32() < 1 || !root.TryGetProperty("components", out var components))
            {
                throw new FormatException("not libevents metadata");
            }

            var events = new Dictionary<uint, EventDefinitions.Definition>();
            var enums = new Dictionary<string, EventEnum>(StringComparer.Ordinal);
            var modeGroups = new Dictionary<uint, int>();
            foreach (var component in components.EnumerateObject())
            {
                var componentId = uint.Parse(component.Name, CultureInfo.InvariantCulture);
                var value = component.Value;
                var space = value.TryGetProperty("namespace", out var ns) ? ns.GetString() ?? string.Empty : string.Empty;

                if (value.TryGetProperty("enums", out var enumList))
                {
                    foreach (var @enum in enumList.EnumerateObject())
                    {
                        enums[space + "::" + @enum.Name] = ReadEnum(@enum.Value);
                    }
                }

                if (value.TryGetProperty("event_groups", out var groups))
                {
                    foreach (var group in groups.EnumerateObject())
                    {
                        if (!group.Value.TryGetProperty("events", out var list))
                        {
                            continue;
                        }

                        foreach (var @event in list.EnumerateObject())
                        {
                            var id = (componentId << 24) | (uint.Parse(@event.Name, CultureInfo.InvariantCulture) & 0xFFFFFF);
                            var e = @event.Value;
                            var arguments = e.TryGetProperty("arguments", out var args)
                                ? args.EnumerateArray().Select(a => a.GetProperty("type").GetString() ?? string.Empty).ToList()
                                : [];
                            events[id] = new EventDefinitions.Definition(
                                Text(e, "name"), space, group.Name, Text(e, "type"), Text(e, "message"), Text(e, "description"), arguments);
                        }
                    }
                }

                if (value.TryGetProperty("navigation_mode_groups", out var navigation) && navigation.TryGetProperty("groups", out var navigationGroups))
                {
                    foreach (var group in navigationGroups.EnumerateObject())
                    {
                        var index = int.Parse(group.Name, CultureInfo.InvariantCulture);
                        foreach (var mode in group.Value.EnumerateArray())
                        {
                            modeGroups[mode.GetUInt32()] = index;
                        }
                    }
                }
            }

            return new EventDefinitions(events, enums, modeGroups);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            throw new FormatException($"events metadata: {exception.Message}", exception);
        }
    }

    private static EventEnum ReadEnum(JsonElement element)
    {
        var entries = new Dictionary<ulong, (string, string)>();
        if (element.TryGetProperty("entries", out var list))
        {
            foreach (var entry in list.EnumerateObject())
            {
                entries[ulong.Parse(entry.Name, CultureInfo.InvariantCulture)] = (Text(entry.Value, "name"), Text(entry.Value, "description"));
            }
        }

        return new EventEnum(Text(element, "type"), entries,
            element.TryGetProperty("is_bitfield", out var bitfield) && bitfield.GetBoolean(),
            element.TryGetProperty("separator", out var separator) ? separator.GetString() ?? "|" : "|");
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}
