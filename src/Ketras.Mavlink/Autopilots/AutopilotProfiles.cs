using Ketras.Mavlink.Messages;

namespace Ketras.Mavlink.Autopilots;

/// <summary>Registry: picks a profile from a HEARTBEAT's autopilot field, or by name from settings.</summary>
public static class AutopilotProfiles
{
    private static readonly Dictionary<string, Func<IAutopilotProfile>> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["px4"] = () => new Px4Profile(),
        ["generic"] = () => new GenericProfile()
    };

    /// <summary>Profile for a name ("px4", "generic"); null for "auto" or an unknown name.</summary>
    public static IAutopilotProfile? FromName(string? name) =>
        name is not null && ByName.TryGetValue(name, out var create) ? create() : null;

    /// <summary>Profile matching a HEARTBEAT autopilot field.</summary>
    public static IAutopilotProfile FromAutopilot(MavAutopilot autopilot) => autopilot switch
    {
        MavAutopilot.Px4 => new Px4Profile(),
        _ => new GenericProfile()
    };

    /// <summary>Adds or replaces a named profile, for autopilots supported outside this library.</summary>
    public static void Register(string name, Func<IAutopilotProfile> create) => ByName[name] = create;
}
