using System.Globalization;

namespace Ketras.Comms;

/// <summary>How a link reaches the vehicle.</summary>
public enum LinkKind
{
    /// <summary>UDP: listen on a local port, or talk to a fixed host.</summary>
    Udp,

    /// <summary>TCP client.</summary>
    Tcp,

    /// <summary>Serial port: a telemetry radio or the autopilot's USB port.</summary>
    Serial,

    /// <summary>A simulator the station runs; connecting starts it when needed.</summary>
    Simulator
}

/// <summary>A saved way to reach a vehicle.</summary>
public sealed record LinkProfile
{
    /// <summary>Baud rates offered for serial links; 57600 is the SiK radio default.</summary>
    public static IReadOnlyList<int> BaudRates { get; } = [9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    /// <summary>Name shown to the operator; unique among saved profiles.</summary>
    public required string Name { get; init; }

    /// <summary>How the link reaches the vehicle.</summary>
    public LinkKind Kind { get; init; }

    /// <summary>UDP: the fixed peer, or null to listen and answer whoever sends. TCP: the server.</summary>
    public string? Host { get; init; }

    /// <summary>UDP: the local port when listening, else the peer's port. TCP: the server's port.</summary>
    public int Port { get; init; } = 14550;

    /// <summary>Serial: the port name, e.g. COM5 or /dev/ttyUSB0.</summary>
    public string? SerialPort { get; init; }

    /// <summary>Serial: the baud rate.</summary>
    public int BaudRate { get; init; } = 57600;

    /// <summary>Simulator: the id of the simulator it starts and connects to.</summary>
    public string? SimulatorId { get; init; }

    /// <summary>"auto" (from the heartbeat) or an autopilot profile name such as "px4".</summary>
    public string Autopilot { get; init; } = "auto";

    /// <summary>Follow only this system id; null follows every autopilot heard.</summary>
    public byte? TargetSystemId { get; init; }

    /// <summary>The Ketras.Mavlink address of a UDP, TCP or serial profile; a simulator profile has none.</summary>
    public string Address => Kind switch
    {
        LinkKind.Udp when string.IsNullOrWhiteSpace(Host) => string.Create(CultureInfo.InvariantCulture, $"udp://:{Port}"),
        LinkKind.Udp => string.Create(CultureInfo.InvariantCulture, $"udp://{Host}:{Port}"),
        LinkKind.Tcp => string.Create(CultureInfo.InvariantCulture, $"tcp://{Host}:{Port}"),
        LinkKind.Serial => string.Create(CultureInfo.InvariantCulture, $"serial:{SerialPort}:{BaudRate}"),
        _ => string.Empty
    };

    /// <summary>Serial ports present on this machine, sorted.</summary>
    public static IReadOnlyList<string> SerialPorts() =>
        System.IO.Ports.SerialPort.GetPortNames().Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Where the link stands.</summary>
public enum LinkState
{
    /// <summary>No link is open.</summary>
    Disconnected,

    /// <summary>Opening, or open and waiting for the first vehicle (a simulator may still be starting).</summary>
    Connecting,

    /// <summary>A vehicle's heartbeats arrive.</summary>
    Connected,

    /// <summary>Vehicles were heard but none answers now, or the transport failed; the link stays open.</summary>
    Lost
}

/// <summary>The link's state, its profile and the last error.</summary>
/// <param name="State">Where it stands.</param>
/// <param name="Profile">The profile it runs on; null while disconnected.</param>
/// <param name="Error">Why the last attempt failed or the link was lost.</param>
/// <param name="Detail">Progress text while connecting, e.g. a simulator's start.</param>
public sealed record LinkStatus(LinkState State, LinkProfile? Profile, string? Error = null, string? Detail = null)
{
    /// <summary>No link and no error.</summary>
    public static LinkStatus Disconnected { get; } = new(LinkState.Disconnected, null);
}
