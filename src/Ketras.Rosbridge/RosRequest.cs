using System.Text.Json.Nodes;

namespace Ketras.Rosbridge;

/// <summary>How a <see cref="RosRequest"/> reaches ROS.</summary>
public enum RosInteraction
{
    /// <summary>Publish one message on a topic.</summary>
    Publish,

    /// <summary>Call a service and wait for its response.</summary>
    CallService,
}

/// <summary>
/// One ROS action: publish on a topic or call a service. <see cref="Parameters"/> are field
/// values as text, keyed by field name (dotted for nested fields, e.g. <c>"position.x"</c>); they
/// are converted to the field's real type before sending.
/// </summary>
/// <param name="Interaction">Publish or call.</param>
/// <param name="Name">Topic or service name.</param>
/// <param name="Type">Message or service type, e.g. <c>std_msgs/msg/String</c>, <c>std_srvs/srv/SetBool</c>.</param>
/// <param name="Parameters">Field values as text; null sends an empty message.</param>
public sealed record RosRequest(RosInteraction Interaction, string Name, string Type, IReadOnlyDictionary<string, string>? Parameters = null)
{
    /// <summary>Parses the rosbridge operation names <c>"publish"</c> and <c>"call_service"</c>.</summary>
    public static bool TryParseInteraction(string? text, out RosInteraction interaction)
    {
        switch (text)
        {
            case "publish":
                interaction = RosInteraction.Publish;
                return true;
            case "call_service":
                interaction = RosInteraction.CallService;
                return true;
            default:
                interaction = default;
                return false;
        }
    }
}

/// <summary>Outcome of a <see cref="RosRequest"/>.</summary>
public enum RosOutcome
{
    /// <summary>Published, or the service reported success.</summary>
    Accepted,

    /// <summary>The service answered and reported failure.</summary>
    Rejected,

    /// <summary>Not sent or not executed: no connection, unknown type, bad value, rosbridge error.</summary>
    Failed,

    /// <summary>The service did not answer in time.</summary>
    NoResponse,
}

/// <summary>Result of <see cref="RosDispatcher.SendAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Reason">Human-readable detail; the service's own message when it gave one.</param>
/// <param name="Values">The service response, when there was one.</param>
public sealed record RosResult(RosOutcome Outcome, string Reason, JsonNode? Values = null)
{
    /// <summary>True for <see cref="RosOutcome.Accepted"/>.</summary>
    public bool IsAccepted => Outcome == RosOutcome.Accepted;
}

/// <summary>Settings for <see cref="RosDispatcher"/>.</summary>
public sealed class RosDispatcherOptions
{
    /// <summary>Wait between reconnection attempts.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Limit for one connection attempt.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a service call may take before <see cref="RosOutcome.NoResponse"/>.</summary>
    public TimeSpan ServiceTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a field-type lookup through rosapi may take.</summary>
    public TimeSpan TypeLookupTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
