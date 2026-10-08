using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Ketras.Mavlink.Autopilots;
using Ketras.Mavlink.Framing;
using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Transport;

namespace Ketras.Mavlink.Testing;

/// <summary>A command the fake vehicle received.</summary>
/// <param name="Command">MAV_CMD.</param>
/// <param name="TargetComponent">Component it was addressed to.</param>
/// <param name="Params">Parameters 1–7.</param>
/// <param name="Confirmation">Confirmation counter (0 for the first send).</param>
public sealed record FakeCommand(MavCmd Command, byte TargetComponent, IReadOnlyList<float> Params, byte Confirmation);

/// <summary>One mission, fence or rally item as the fake vehicle stores it.</summary>
/// <param name="Frame">MAV_FRAME.</param>
/// <param name="Command">MAV_CMD.</param>
/// <param name="AutoContinue">Autocontinue flag.</param>
/// <param name="Param1">Parameter 1.</param>
/// <param name="Param2">Parameter 2.</param>
/// <param name="Param3">Parameter 3.</param>
/// <param name="Param4">Parameter 4.</param>
/// <param name="X">Latitude × 1e7 for global frames.</param>
/// <param name="Y">Longitude × 1e7 for global frames.</param>
/// <param name="Z">Altitude.</param>
public sealed record FakeMissionItem(MavFrame Frame, MavCmd Command, bool AutoContinue, float Param1, float Param2, float Param3, float Param4, int X, int Y, float Z);

/// <summary>How the fake vehicle starts.</summary>
public sealed class FakeVehicleOptions
{
    /// <summary>MAVLink system id.</summary>
    public byte SystemId { get; init; } = 1;

    /// <summary>Autopilot component id.</summary>
    public byte ComponentId { get; init; } = 1;

    /// <summary>Vehicle type in the heartbeat.</summary>
    public MavType Type { get; init; } = MavType.FixedWing;

    /// <summary>Heartbeat interval; shorter than a real vehicle's 1 s so tests run fast.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Initial parameters: name → (type, value).</summary>
    public IReadOnlyDictionary<string, (MavParamType Type, double Value)> Parameters { get; init; } =
        new Dictionary<string, (MavParamType, double)>();

    /// <summary>Firmware "major.minor.patch" reported in AUTOPILOT_VERSION.</summary>
    public Version Firmware { get; init; } = new(1, 15, 0);
}

/// <summary>
/// A PX4-like vehicle behind an <see cref="InMemoryLink"/>: heartbeat with PX4 custom modes, arming, REQUEST_MESSAGE,
/// the parameter protocol (bytewise encoding), mission/fence/rally transfer, read-only MAVLink FTP and the events
/// protocol (events kept for REQUEST_EVENT, CURRENT_EVENT_SEQUENCE with each heartbeat). Tests drive it
/// through the station's side (<see cref="Transport"/>) and inspect or inject state here. Thread-safe.
/// </summary>
public sealed class FakeVehicle : IAsyncDisposable
{
    private const byte SafetyArmed = 128;
    private const byte CustomModeEnabled = 1;
    private const string HashCheck = "_HASH_CHECK";

    private readonly FakeVehicleOptions _options;
    private readonly InMemoryLink _link = InMemoryLink.Create();
    private readonly MavlinkParser _parser = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<(string Name, MavParamType Type, double Value)> _parameters;
    private readonly Dictionary<MavMissionType, List<FakeMissionItem>> _lists = new();
    private readonly ConcurrentQueue<FakeCommand> _commands = new();
    private readonly Px4Profile _profile = new();
    private Task? _heartbeat;
    private byte _sequence;
    private (MavMissionType Type, int Count, List<FakeMissionItem> Items)? _upload;
    private byte _ftpSession;
    private byte[]? _ftpOpen;
    private readonly Dictionary<ushort, (uint Id, byte LogLevels, byte[] Arguments, uint TimeMs)> _events = new();
    private readonly List<ushort> _eventRequests = [];
    private ushort _eventSequence;
    private ushort _statusTextId;
    private bool _hasEvents;

    /// <summary>Creates the vehicle; it starts sending once <see cref="StartAsync"/> runs.</summary>
    public FakeVehicle(FakeVehicleOptions? options = null)
    {
        _options = options ?? new FakeVehicleOptions();
        _parameters = _options.Parameters.Select(p => (p.Key, p.Value.Type, p.Value.Value)).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        _link.Vehicle.Received += OnReceived;
        Mode = "AUTO.LOITER";
    }

    /// <summary>The station's end of the link; pass it to <c>MavlinkConnection.Create</c>.</summary>
    public IMavlinkTransport Transport => _link.Station;

    /// <summary>The station's end, for failure injection.</summary>
    public InMemoryLink.End StationEnd => _link.Station;

    /// <summary>The vehicle's end, for failure injection.</summary>
    public InMemoryLink.End VehicleEnd => _link.Vehicle;

    /// <summary>MAVLink system id.</summary>
    public byte SystemId => _options.SystemId;

    /// <summary>Current PX4 mode name.</summary>
    public string Mode { get; private set; }

    /// <summary>Armed flag.</summary>
    public bool IsArmed { get; private set; }

    /// <summary>While set, no heartbeat is sent, so the station's watchdog sees the link as lost.</summary>
    public bool HeartbeatPaused { get; set; }

    /// <summary>While set, commands get no COMMAND_ACK.</summary>
    public bool IgnoreCommands { get; set; }

    /// <summary>Result for COMPONENT_ARM_DISARM; null accepts.</summary>
    public MavResult? ArmingResult { get; set; }

    /// <summary>Result for DO_SET_MODE to a known mode; null accepts.</summary>
    public MavResult? ModeResult { get; set; }

    /// <summary>Indices left out of the first full parameter stream, to exercise re-requests.</summary>
    public ISet<int> DropFromParameterList { get; } = new HashSet<int>();

    /// <summary>Parameters whose writes are refused: the echo carries the old value.</summary>
    public ISet<string> ReadOnlyParameters { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Result for a completed upload; null accepts.</summary>
    public MavMissionResult? UploadResult { get; set; }

    /// <summary>Files served over MAVLink FTP, by absolute path.</summary>
    public IDictionary<string, byte[]> Files { get; } = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);

    /// <summary>Commands acknowledged as ACCEPTED without any effect (calibrations, reboot); the test sends what the vehicle would report.</summary>
    public ISet<MavCmd> AcceptedCommands { get; } = new HashSet<MavCmd>();

    /// <summary>The COMPONENT_METADATA answer (uri, crc); null answers REQUEST_MESSAGE 397 with UNSUPPORTED.</summary>
    public (string Uri, uint Crc)? ComponentMetadata { get; set; }

    /// <summary>The home position sent on request; null leaves HOME_POSITION unanswered.</summary>
    public (double Latitude, double Longitude, double AltitudeAmsl)? Home { get; set; }

    /// <summary>Events kept but not sent, to exercise the station's re-request; each <see cref="SendEvent"/> uses one.</summary>
    public int DropNextEvents { get; set; }

    /// <summary>Raised when MAV_CMD_RUN_PREARM_CHECKS arrives (it is accepted); a test sends the report here.</summary>
    public event Action? PrearmChecksRequested;

    /// <summary>REQUEST_EVENT messages received so far (first sequence of each).</summary>
    public IReadOnlyList<ushort> EventRequests
    {
        get
        {
            lock (_lock)
            {
                return [.. _eventRequests];
            }
        }
    }

    /// <summary>Commands received so far, oldest first.</summary>
    public IReadOnlyList<FakeCommand> Commands => _commands.ToArray();

    /// <summary>Starts the link and the heartbeat.</summary>
    public async Task StartAsync()
    {
        await _link.Vehicle.OpenAsync();
        _heartbeat = Task.Run(HeartbeatLoopAsync, CancellationToken.None);
    }

    /// <summary>The vehicle's stored list of a type.</summary>
    public IReadOnlyList<FakeMissionItem> Items(MavMissionType type)
    {
        lock (_lock)
        {
            return _lists.TryGetValue(type, out var items) ? items.ToList() : [];
        }
    }

    /// <summary>Stores a list as if another station had uploaded it.</summary>
    public void SetItems(MavMissionType type, IEnumerable<FakeMissionItem> items)
    {
        lock (_lock)
        {
            _lists[type] = items.ToList();
        }
    }

    /// <summary>Current value of a parameter.</summary>
    public double? Parameter(string name)
    {
        lock (_lock)
        {
            var index = _parameters.FindIndex(p => p.Name == name);
            return index < 0 ? null : _parameters[index].Value;
        }
    }

    /// <summary>Sends GLOBAL_POSITION_INT.</summary>
    public void SendPosition(double latitude, double longitude, double altitudeAmsl, double relativeAltitude, double? headingDegrees = null,
        double northMps = 0, double eastMps = 0) =>
        Send(new GlobalPositionIntMessage
        {
            Lat = (int)Math.Round(latitude * 1e7),
            Lon = (int)Math.Round(longitude * 1e7),
            Alt = (int)Math.Round(altitudeAmsl * 1000),
            RelativeAlt = (int)Math.Round(relativeAltitude * 1000),
            Vx = (short)Math.Round(northMps * 100),
            Vy = (short)Math.Round(eastMps * 100),
            Hdg = headingDegrees is { } heading ? (ushort)Math.Round(heading * 100) : ushort.MaxValue
        });

    /// <summary>Sets <see cref="Home"/> and sends HOME_POSITION.</summary>
    public void SendHome(double latitude, double longitude, double altitudeAmsl)
    {
        Home = (latitude, longitude, altitudeAmsl);
        SendHomePosition();
    }

    /// <summary>Sends ATTITUDE (degrees).</summary>
    public void SendAttitude(double rollDegrees, double pitchDegrees, double yawDegrees) =>
        Send(new AttitudeMessage
        {
            Roll = (float)(rollDegrees * Math.PI / 180),
            Pitch = (float)(pitchDegrees * Math.PI / 180),
            Yaw = (float)(yawDegrees * Math.PI / 180)
        });

    /// <summary>Sends RC_CHANNELS with the given PWM values (up to 18).</summary>
    public void SendRc(params ushort[] pwm)
    {
        ushort At(int i) => i < pwm.Length ? pwm[i] : (ushort)0;
        Send(new RcChannelsMessage
        {
            Chancount = (byte)Math.Min(pwm.Length, 18),
            Chan1Raw = At(0), Chan2Raw = At(1), Chan3Raw = At(2), Chan4Raw = At(3), Chan5Raw = At(4), Chan6Raw = At(5),
            Chan7Raw = At(6), Chan8Raw = At(7), Chan9Raw = At(8), Chan10Raw = At(9), Chan11Raw = At(10), Chan12Raw = At(11),
            Chan13Raw = At(12), Chan14Raw = At(13), Chan15Raw = At(14), Chan16Raw = At(15), Chan17Raw = At(16), Chan18Raw = At(17),
            Rssi = 255
        });
    }

    /// <summary>Sends BATTERY_STATUS (battery 0) with one cell voltage (the pack, as PX4 without cell measurement), the
    /// current, the charge used and the time left (0: not estimated); no temperature.</summary>
    public void SendBattery(double volts, double amps, int remainingPercent, double consumedMah = 0, int timeRemainingSeconds = 0)
    {
        var cells = new ushort[10];
        Array.Fill(cells, ushort.MaxValue);
        cells[0] = (ushort)Math.Round(volts * 1000);
        Send(new BatteryStatusMessage
        {
            Id = 0, Voltages = cells, CurrentBattery = (short)Math.Round(amps * 100), BatteryRemaining = (sbyte)remainingPercent,
            CurrentConsumed = (int)Math.Round(consumedMah), TimeRemaining = timeRemainingSeconds, Temperature = short.MaxValue
        });
    }

    /// <summary>Sends VFR_HUD.</summary>
    public void SendHud(double airspeedMps, double climbMps, int throttlePercent) =>
        Send(new VfrHudMessage { Airspeed = (float)airspeedMps, Climb = (float)climbMps, Throttle = (ushort)throttlePercent });

    /// <summary>Sends NAV_CONTROLLER_OUTPUT as PX4 fills it (altitude error: setpoint − altitude; airspeed error: airspeed − setpoint).</summary>
    public void SendNavigation(double waypointDistanceMeters, double targetBearingDegrees, double altitudeErrorMeters, double airspeedErrorMps,
        double crossTrackErrorMeters) =>
        Send(new NavControllerOutputMessage
        {
            WpDist = (ushort)Math.Round(waypointDistanceMeters), TargetBearing = (short)Math.Round(targetBearingDegrees),
            AltError = (float)altitudeErrorMeters, AspdError = (float)airspeedErrorMps, XtrackError = (float)crossTrackErrorMeters
        });

    /// <summary>Sends ESTIMATOR_STATUS with every test ratio at <paramref name="ratio"/> except the horizontal position one.</summary>
    public void SendEstimator(double ratio, double horizontalPositionRatio) =>
        Send(new EstimatorStatusMessage
        {
            VelRatio = (float)ratio, PosHorizRatio = (float)horizontalPositionRatio, PosVertRatio = (float)ratio, MagRatio = (float)ratio,
            HaglRatio = (float)ratio, TasRatio = (float)ratio
        });

    /// <summary>Sends VIBRATION with one level on every axis and a clipping count on the first accelerometer.</summary>
    public void SendVibration(double level, uint clipping = 0) =>
        Send(new VibrationMessage { VibrationX = (float)level, VibrationY = (float)level, VibrationZ = (float)level, Clipping0 = clipping });

    /// <summary>Sends STATUSTEXT as PX4 does: a text longer than 50 characters in 50-character chunks under one id.</summary>
    public void SendStatusText(MavSeverity severity, string text)
    {
        ushort id;
        lock (_lock)
        {
            id = ++_statusTextId;
        }

        var chunk = 0;
        do
        {
            var part = text.Substring(chunk * 50, Math.Min(50, text.Length - (chunk * 50)));
            Send(new StatustextMessage { Severity = (byte)severity, Text = part, Id = id, ChunkSeq = (byte)chunk });
            chunk++;
        }
        while (chunk * 50 < text.Length);
    }

    /// <summary>
    /// Sends an EVENT with the next sequence number (kept for REQUEST_EVENT), unless <see cref="DropNextEvents"/> holds
    /// it back. <paramref name="logLevels"/> packs the external level in the low 4 bits, as libevents does.
    /// </summary>
    public void SendEvent(uint id, byte logLevels, byte[] arguments)
    {
        ushort sequence;
        bool drop;
        lock (_lock)
        {
            sequence = _eventSequence++;
            _hasEvents = true;
            var padded = new byte[40];
            arguments.AsSpan(0, Math.Min(arguments.Length, 40)).CopyTo(padded);
            _events[sequence] = (id, logLevels, padded, (uint)Environment.TickCount64);
            drop = DropNextEvents > 0;
            if (drop)
            {
                DropNextEvents--;
            }
        }

        if (!drop)
        {
            SendStoredEvent(sequence);
        }
    }

    /// <summary>Forgets the events before <paramref name="sequence"/>, as a vehicle whose buffer moved on.</summary>
    public void ForgetEventsBefore(ushort sequence)
    {
        lock (_lock)
        {
            foreach (var key in _events.Keys.Where(key => key < sequence).ToList())
            {
                _events.Remove(key);
            }
        }
    }

    /// <summary>Sends a kept event again, as a vehicle repeating itself.</summary>
    public void ResendEvent(ushort sequence) => SendStoredEvent(sequence);

    /// <summary>Sends CURRENT_EVENT_SEQUENCE with the latest sequence number.</summary>
    public void SendCurrentEventSequence()
    {
        ushort latest;
        lock (_lock)
        {
            if (!_hasEvents)
            {
                return;
            }

            latest = (ushort)(_eventSequence - 1);
        }

        Send(new CurrentEventSequenceMessage { Sequence = latest });
    }

    /// <summary>Sends EXTENDED_SYS_STATE.</summary>
    public void SendLandedState(MavLandedState state) =>
        Send(new ExtendedSysStateMessage { LandedState = (byte)state });

    /// <summary>Sends MISSION_CURRENT.</summary>
    public void SendMissionCurrent(int sequence, int total) =>
        Send(new MissionCurrentMessage { Seq = (ushort)sequence, Total = (ushort)total });

    /// <summary>Sends WIND_COV (air velocity in NED, m/s).</summary>
    public void SendWind(double northMps, double eastMps, double downMps) =>
        Send(new WindCovMessage { WindX = (float)northMps, WindY = (float)eastMps, WindZ = (float)downMps });

    /// <summary>Sends HYGROMETER_SENSOR the way PX4 does (temperature in cdegC, humidity in whole percent).</summary>
    public void SendAir(double temperatureCelsius, double humidityPercent) =>
        Send(new HygrometerSensorMessage { Temperature = (short)Math.Round(temperatureCelsius * 100), Humidity = (ushort)Math.Round(humidityPercent) });

    /// <summary>Sends GPS_RAW_INT with a fix type (GPS_FIX_TYPE value) and satellite count.</summary>
    public void SendGps(byte fixType, byte satellites, double hdop) =>
        Send(new GpsRawIntMessage { FixType = fixType, SatellitesVisible = satellites, Eph = (ushort)Math.Round(hdop * 100), Epv = ushort.MaxValue });

    /// <summary>Sends ADSB_VEHICLE with every field marked valid.</summary>
    public void SendAdsb(uint icao, string callsign, double latitude, double longitude, double altitudeMeters, double headingDegrees, double speedMps) =>
        Send(new AdsbVehicleMessage
        {
            IcaoAddress = icao,
            Callsign = callsign,
            Lat = (int)Math.Round(latitude * 1e7),
            Lon = (int)Math.Round(longitude * 1e7),
            Altitude = (int)Math.Round(altitudeMeters * 1000),
            AltitudeType = (byte)AdsbAltitudeType.Geometric,
            Heading = (ushort)Math.Round(headingDegrees * 100),
            HorVelocity = (ushort)Math.Round(speedMps * 100),
            EmitterType = (byte)AdsbEmitterType.Light,
            Flags = (ushort)(AdsbFlags.ValidCoords | AdsbFlags.ValidAltitude | AdsbFlags.ValidHeading | AdsbFlags.ValidVelocity | AdsbFlags.ValidCallsign)
        });

    private void OnReceived(ReadOnlyMemory<byte> data)
    {
        IReadOnlyList<MavlinkPacket> packets;
        lock (_parser)
        {
            packets = _parser.Feed(data.Span);
        }

        foreach (var packet in packets)
        {
            Handle(packet.Message);
        }
    }

    private void Handle(IMavlinkMessage message)
    {
        switch (message)
        {
            case CommandLongMessage command when command.TargetSystem == SystemId:
                HandleCommand(command);
                break;
            case ParamRequestListMessage list when list.TargetSystem == SystemId:
                SendParameterList();
                break;
            case ParamRequestReadMessage read when read.TargetSystem == SystemId:
                SendParameter(read.ParamIndex >= 0 ? read.ParamIndex : IndexOf(read.ParamId));
                break;
            case ParamSetMessage set when set.TargetSystem == SystemId:
                WriteParameter(set);
                break;
            case MissionCountMessage count when count.TargetSystem == SystemId:
                BeginUpload((MavMissionType)count.MissionType, count.Count);
                break;
            case MissionItemIntMessage item when item.TargetSystem == SystemId:
                ReceiveItem(item);
                break;
            case MissionRequestListMessage request when request.TargetSystem == SystemId:
                Send(new MissionCountMessage { Count = (ushort)Items((MavMissionType)request.MissionType).Count, MissionType = request.MissionType });
                break;
            case MissionRequestIntMessage request when request.TargetSystem == SystemId:
                SendItem((MavMissionType)request.MissionType, request.Seq);
                break;
            case MissionClearAllMessage clear when clear.TargetSystem == SystemId:
                lock (_lock)
                {
                    _lists.Remove((MavMissionType)clear.MissionType);
                }

                Send(new MissionAckMessage { Type = (byte)MavMissionResult.MavMissionAccepted, MissionType = clear.MissionType });
                break;
            case FileTransferProtocolMessage ftp when ftp.TargetSystem == SystemId:
                HandleFtp(ftp.Payload);
                break;
            case RequestEventMessage request when request.TargetSystem == SystemId:
                lock (_lock)
                {
                    _eventRequests.Add(request.FirstSequence);
                }

                SendStoredEvent(request.FirstSequence);
                break;
        }
    }

    private void SendStoredEvent(ushort sequence)
    {
        (uint Id, byte LogLevels, byte[] Arguments, uint TimeMs) stored;
        ushort oldest;
        lock (_lock)
        {
            if (!_events.TryGetValue(sequence, out stored))
            {
                oldest = _events.Count == 0 ? _eventSequence : _events.Keys.Where(key => key > sequence).DefaultIfEmpty(_eventSequence).Min();
                stored = default;
            }
            else
            {
                oldest = 0;
            }
        }

        if (stored.Arguments is null)
        {
            Send(new ResponseEventErrorMessage { TargetSystem = 255, TargetComponent = 190, Sequence = sequence, SequenceOldestAvailable = oldest, Reason = 0 });
            return;
        }

        Send(new EventMessage
        {
            DestinationSystem = 0,
            DestinationComponent = 0,
            Id = stored.Id,
            EventTimeBootMs = stored.TimeMs,
            Sequence = sequence,
            LogLevels = stored.LogLevels,
            Arguments = stored.Arguments
        });
    }

    private void HandleCommand(CommandLongMessage command)
    {
        var cmd = (MavCmd)command.Command;
        _commands.Enqueue(new FakeCommand(cmd, command.TargetComponent,
            [command.Param1, command.Param2, command.Param3, command.Param4, command.Param5, command.Param6, command.Param7], command.Confirmation));
        if (IgnoreCommands)
        {
            return;
        }

        var result = AcceptedCommands.Contains(cmd) ? MavResult.Accepted : MavResult.Unsupported;
        switch (cmd)
        {
            case MavCmd.DoSetMode:
                var name = _profile.ModeName((byte)command.Param1, ((uint)command.Param2 << 16) | ((uint)command.Param3 << 24));
                if (!name.StartsWith("CUSTOM", StringComparison.Ordinal))
                {
                    result = ModeResult ?? MavResult.Accepted;
                    if (result == MavResult.Accepted)
                    {
                        Mode = name;
                    }
                }
                else
                {
                    result = MavResult.Denied;
                }

                break;
            case MavCmd.ComponentArmDisarm:
                result = ArmingResult ?? MavResult.Accepted;
                if (result == MavResult.Accepted)
                {
                    IsArmed = command.Param1 >= 0.5f;
                }

                break;
            case MavCmd.RequestMessage:
                result = AnswerRequest((uint)command.Param1);
                break;
            case MavCmd.RunPrearmChecks:
                result = MavResult.Accepted;
                break;
        }

        Send(new CommandAckMessage { Command = command.Command, Result = (byte)result, TargetSystem = 255, TargetComponent = 190 });
        SendHeartbeat();
        if (cmd == MavCmd.RunPrearmChecks)
        {
            PrearmChecksRequested?.Invoke();
        }
    }

    private MavResult AnswerRequest(uint messageId)
    {
        switch (messageId)
        {
            case HomePositionMessage.WireId when Home is not null:
                SendHomePosition();
                return MavResult.Accepted;
            case AutopilotVersionMessage.WireId:
                var hash = new byte[8];
                for (var i = 0; i < 8; i++)
                {
                    hash[i] = (byte)(0x10 + i);
                }

                Send(new AutopilotVersionMessage
                {
                    FlightSwVersion = ((uint)_options.Firmware.Major << 24) | ((uint)_options.Firmware.Minor << 16) | ((uint)_options.Firmware.Build << 8) | 0xFF,
                    FlightCustomVersion = hash
                });
                return MavResult.Accepted;
            case ComponentMetadataMessage.WireId when ComponentMetadata is { } metadata:
                Send(new ComponentMetadataMessage { Uri = metadata.Uri, FileCrc = metadata.Crc });
                return MavResult.Accepted;
            default:
                return MavResult.Unsupported;
        }
    }

    private void SendHomePosition()
    {
        if (Home is { } home)
        {
            Send(new HomePositionMessage
            {
                Latitude = (int)Math.Round(home.Latitude * 1e7),
                Longitude = (int)Math.Round(home.Longitude * 1e7),
                Altitude = (int)Math.Round(home.AltitudeAmsl * 1000),
                Q = [1, 0, 0, 0]
            });
        }
    }

    private int IndexOf(string name)
    {
        lock (_lock)
        {
            return _parameters.FindIndex(p => p.Name == name);
        }
    }

    private void SendParameterList()
    {
        int count;
        lock (_lock)
        {
            count = _parameters.Count;
        }

        for (var i = 0; i < count; i++)
        {
            if (!DropFromParameterList.Remove(i))
            {
                SendParameter(i);
            }
        }

        // PX4 closes a full list with _HASH_CHECK at index -1 (mavlink_parameters.cpp).
        Send(new ParamValueMessage { ParamId = HashCheck, ParamType = (byte)MavParamType.Uint32, ParamIndex = ushort.MaxValue, ParamCount = (ushort)count });
    }

    private void SendParameter(int index)
    {
        (string Name, MavParamType Type, double Value) parameter;
        int count;
        lock (_lock)
        {
            if (index < 0 || index >= _parameters.Count)
            {
                return;
            }

            parameter = _parameters[index];
            count = _parameters.Count;
        }

        Send(new ParamValueMessage
        {
            ParamId = parameter.Name,
            ParamType = (byte)parameter.Type,
            ParamValue = _profile.EncodeParameter(new ParameterValue(parameter.Type, parameter.Value)),
            ParamIndex = (ushort)index,
            ParamCount = (ushort)count
        });
    }

    private void WriteParameter(ParamSetMessage set)
    {
        int index;
        lock (_lock)
        {
            index = _parameters.FindIndex(p => p.Name == set.ParamId);
            if (index >= 0 && !ReadOnlyParameters.Contains(set.ParamId) && (MavParamType)set.ParamType == _parameters[index].Type)
            {
                var decoded = _profile.DecodeParameter(set.ParamValue, _parameters[index].Type);
                _parameters[index] = (_parameters[index].Name, _parameters[index].Type, decoded.Value);
            }
        }

        SendParameter(index);
    }

    private void BeginUpload(MavMissionType type, int count)
    {
        if (count == 0)
        {
            lock (_lock)
            {
                _lists[type] = [];
            }

            Send(new MissionAckMessage { Type = (byte)MavMissionResult.MavMissionAccepted, MissionType = (byte)type });
            return;
        }

        lock (_lock)
        {
            _upload = (type, count, []);
        }

        Send(new MissionRequestIntMessage { Seq = 0, MissionType = (byte)type });
    }

    private void ReceiveItem(MissionItemIntMessage item)
    {
        MavMissionType type;
        int next;
        bool done;
        lock (_lock)
        {
            if (_upload is not { } upload || item.MissionType != (byte)upload.Type || item.Seq != upload.Items.Count)
            {
                return;
            }

            upload.Items.Add(new FakeMissionItem((MavFrame)item.Frame, (MavCmd)item.Command, item.Autocontinue != 0,
                item.Param1, item.Param2, item.Param3, item.Param4, item.X, item.Y, item.Z));
            type = upload.Type;
            next = upload.Items.Count;
            done = next == upload.Count;
            if (done)
            {
                if (UploadResult is null)
                {
                    _lists[type] = upload.Items;
                }

                _upload = null;
            }
        }

        if (done)
        {
            Send(new MissionAckMessage { Type = (byte)(UploadResult ?? MavMissionResult.MavMissionAccepted), MissionType = (byte)type });
        }
        else
        {
            Send(new MissionRequestIntMessage { Seq = (ushort)next, MissionType = (byte)type });
        }
    }

    private void SendItem(MavMissionType type, int seq)
    {
        var items = Items(type);
        if (seq >= items.Count)
        {
            return;
        }

        var item = items[seq];
        Send(new MissionItemIntMessage
        {
            Seq = (ushort)seq,
            Frame = (byte)item.Frame,
            Command = (ushort)item.Command,
            Autocontinue = (byte)(item.AutoContinue ? 1 : 0),
            Param1 = item.Param1,
            Param2 = item.Param2,
            Param3 = item.Param3,
            Param4 = item.Param4,
            X = item.X,
            Y = item.Y,
            Z = item.Z,
            MissionType = (byte)type
        });
    }

    /// <summary>Read-only MAVLink FTP: OpenFileRO, ReadFile, TerminateSession (PX4 mavlink_ftp.h layout).</summary>
    private void HandleFtp(byte[] request)
    {
        const int header = 12;
        var sequence = BinaryPrimitives.ReadUInt16LittleEndian(request);
        var opcode = request[3];
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(8, 4));
        var reply = new byte[251];
        BinaryPrimitives.WriteUInt16LittleEndian(reply, (ushort)(sequence + 1));
        reply[2] = request[2];
        reply[5] = opcode;

        void Nak(byte error)
        {
            reply[3] = 129;
            reply[4] = 1;
            reply[header] = error;
        }

        switch (opcode)
        {
            case 4:
                var path = Encoding.ASCII.GetString(request, header, request[4]);
                if (Files.TryGetValue(path, out var file))
                {
                    _ftpOpen = file;
                    reply[2] = ++_ftpSession;
                    reply[3] = 128;
                    reply[4] = 4;
                    BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(header, 4), (uint)file.Length);
                }
                else
                {
                    Nak(10);
                }

                break;
            case 5:
                if (_ftpOpen is not { } open)
                {
                    Nak(4);
                }
                else if (offset >= open.Length)
                {
                    Nak(6);
                }
                else
                {
                    var length = Math.Min(Math.Min((int)request[4], 239), open.Length - (int)offset);
                    open.AsSpan((int)offset, length).CopyTo(reply.AsSpan(header));
                    reply[3] = 128;
                    reply[4] = (byte)length;
                    BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(8, 4), offset);
                }

                break;
            case 1:
                _ftpOpen = null;
                reply[3] = 128;
                break;
            default:
                Nak(7);
                break;
        }

        Send(new FileTransferProtocolMessage { TargetSystem = 255, TargetComponent = 190, Payload = reply });
    }

    private async Task HeartbeatLoopAsync()
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        try
        {
            do
            {
                if (!HeartbeatPaused)
                {
                    SendHeartbeat();
                    SendCurrentEventSequence();
                }
            }
            while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SendHeartbeat()
    {
        _profile.TryGetSetModeParams(Mode, out _, out var main, out var sub);
        Send(new HeartbeatMessage
        {
            Type = (byte)_options.Type,
            Autopilot = (byte)MavAutopilot.Px4,
            BaseMode = (byte)(CustomModeEnabled | (IsArmed ? SafetyArmed : 0)),
            CustomMode = ((uint)main << 16) | ((uint)sub << 24),
            SystemStatus = (byte)(IsArmed ? MavState.Active : MavState.Standby),
            MavlinkVersion = 3
        });
    }

    private void Send(IMavlinkMessage message)
    {
        byte[] frame;
        lock (_lock)
        {
            frame = MavlinkFrame.Encode(message, _sequence++, SystemId, _options.ComponentId);
        }

        try
        {
            _link.Vehicle.SendAsync(frame).AsTask().GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            // FailSends on the vehicle's end: the frame is lost, as on a broken radio.
        }
    }

    /// <summary>Stops the heartbeat and closes the link.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_heartbeat is not null)
        {
            await _heartbeat;
        }

        await _link.Vehicle.DisposeAsync();
    }
}
