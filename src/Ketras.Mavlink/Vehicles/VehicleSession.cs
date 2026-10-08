using Ketras.Mavlink.Autopilots;
using Ketras.Mavlink.Framing;
using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Protocols;
using Microsoft.Extensions.Logging;

namespace Ketras.Mavlink.Vehicles;

/// <summary>
/// Facade over one vehicle (one autopilot system id) on a connection: its autopilot profile, telemetry and protocol
/// clients. Packets from this system are routed here by <see cref="MavlinkConnection"/> on its dispatch thread.
/// </summary>
public sealed class VehicleSession
{
    private long _lastHeartbeatTicks;
    private readonly ILogger _logger;

    internal VehicleSession(Func<IMavlinkMessage, ValueTask> send, byte systemId, byte componentId, MavAutopilot autopilot, MavType type, IAutopilotProfile profile,
        ILogger logger, byte ownSystemId = 255, byte ownComponentId = 190)
    {
        _logger = logger;
        SystemId = systemId;
        ComponentId = componentId;
        Autopilot = autopilot;
        VehicleType = type;
        Profile = profile;
        Status = new VehicleStatus(profile, logger);
        Commands = new CommandClient(send, systemId, componentId, profile, logger);
        Parameters = new ParameterClient(send, systemId, componentId, profile, logger);
        Missions = new MissionClient(send, systemId, componentId);
        Ftp = new FtpClient(send, systemId, componentId);
        Metadata = new ComponentMetadataClient(Commands, Ftp);
        Events = new EventsClient(send, systemId, componentId, ownSystemId, ownComponentId, logger);
        _lastHeartbeatTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>MAVLink system id.</summary>
    public byte SystemId { get; }

    /// <summary>Autopilot component id (usually 1).</summary>
    public byte ComponentId { get; }

    /// <summary>Autopilot firmware family from the first HEARTBEAT.</summary>
    public MavAutopilot Autopilot { get; }

    /// <summary>Vehicle type from the first HEARTBEAT.</summary>
    public MavType VehicleType { get; }

    /// <summary>Autopilot-specific behaviour.</summary>
    public IAutopilotProfile Profile { get; }

    /// <summary>Telemetry and state.</summary>
    public VehicleStatus Status { get; }

    /// <summary>Commands with acknowledgements.</summary>
    public CommandClient Commands { get; }

    /// <summary>Parameter protocol.</summary>
    public ParameterClient Parameters { get; }

    /// <summary>Mission, geofence and rally transfer.</summary>
    public MissionClient Missions { get; }

    /// <summary>MAVLink FTP (file reads).</summary>
    public FtpClient Ftp { get; }

    /// <summary>Component metadata (parameters.json, actuators.json) from the vehicle.</summary>
    public ComponentMetadataClient Metadata { get; }

    /// <summary>The events protocol (PX4 events, health and arming check reports) of the autopilot component.</summary>
    public EventsClient Events { get; }

    /// <summary>When the autopilot's last HEARTBEAT arrived (UTC).</summary>
    public DateTime LastHeartbeatUtc => new(Interlocked.Read(ref _lastHeartbeatTicks), DateTimeKind.Utc);

    /// <summary>True while heartbeats keep arriving within the link timeout and the transport works.</summary>
    public bool IsConnected { get; private set; } = true;

    /// <summary>Raised on the connection's dispatch thread when <see cref="IsConnected"/> changes.</summary>
    public event Action<bool>? ConnectionChanged;

    internal void Handle(MavlinkPacket packet)
    {
        if (packet.Message is HeartbeatMessage && packet.ComponentId == ComponentId)
        {
            Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
            SetConnected(true);
        }

        Status.Handle(packet, ComponentId);

        switch (packet.Message)
        {
            case CommandAckMessage ack:
                Commands.Handle(ack, packet.ComponentId);
                break;
            case ParamValueMessage value when packet.ComponentId == ComponentId:
                Parameters.Handle(value);
                break;
            case FileTransferProtocolMessage ftp:
                Ftp.Handle(ftp);
                break;
            case ComponentMetadataMessage metadata when packet.ComponentId == ComponentId:
                Metadata.Handle(metadata);
                break;
            case EventMessage or CurrentEventSequenceMessage or ResponseEventErrorMessage when packet.ComponentId == ComponentId:
                Events.Handle(packet.Message);
                break;
        }

        if (packet.ComponentId == ComponentId)
        {
            Missions.Handle(packet.Message);
        }
    }

    internal void CheckLink(TimeSpan timeout, DateTime nowUtc)
    {
        Status.ExpireTexts(nowUtc);
        if (nowUtc - LastHeartbeatUtc > timeout)
        {
            SetConnected(false);
        }
    }

    internal void MarkLost() => SetConnected(false);

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected)
        {
            return;
        }

        IsConnected = connected;
        Subscribers.Raise(ConnectionChanged, connected, _logger, nameof(ConnectionChanged));
    }
}
