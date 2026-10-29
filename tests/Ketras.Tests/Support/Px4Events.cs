using System.Buffers.Binary;
using System.Text;
using Ketras.Events;
using Ketras.Mavlink.Protocols;
using Ketras.Mavlink.Testing;

namespace Ketras.Tests.Support;

/// <summary>PX4 v1.17.0's events (Fixtures/px4-v1.17.0-events.json, the build's all_events.json) as a vehicle sends them.</summary>
internal static class Px4Events
{
    public const uint ArmingSummary = (1u << 24) | 11047904;
    public const uint HealthSummary = (1u << 24) | 1914663;
    public const uint NoGlobalPosition = (1u << 24) | 10011251;
    public const uint NoMission = (1u << 24) | 377561;
    public const uint CpuLoad = (1u << 24) | 1058956;
    public const uint ModeSwitchNotPossible = (1u << 24) | 10503063;
    public const uint ArmDenied = (1u << 24) | 133277;

    public const uint Loiter = 1u << 4;
    public const uint Mission = 1u << 3;
    public const uint AllModes = 0x7FFFFFFF;
    public const byte GlobalPosition = 24;
    public const byte MissionComponent = 18;
    public const uint MissionCustomMode = 0x04040000;

    public const byte Error = 3;
    public const byte Protocol = 8;

    public static string Json => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "px4-v1.17.0-events.json"));

    public static EventDefinitions Definitions { get; } = EventsParser.Parse(Json);

    /// <summary>Packs arguments little-endian, as the vehicle does.</summary>
    public static byte[] Args(params object[] values)
    {
        var bytes = new List<byte>();
        foreach (var value in values)
        {
            switch (value)
            {
                case byte b:
                    bytes.Add(b);
                    break;
                case uint u:
                    var u4 = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(u4, u);
                    bytes.AddRange(u4);
                    break;
                case float f:
                    var f4 = new byte[4];
                    BinaryPrimitives.WriteSingleLittleEndian(f4, f);
                    bytes.AddRange(f4);
                    break;
            }
        }

        return [.. bytes];
    }

    public static VehicleEvent Event(uint id, byte level, params object[] values)
    {
        var arguments = new byte[40];
        Args(values).CopyTo(arguments, 0);
        return new VehicleEvent(id, 0, 0, level, arguments);
    }

    /// <summary>A report: the summary, one event per failing check (modes, component), the health summary.</summary>
    public static IEnumerable<VehicleEvent> Report(uint error, uint canArm, uint canRun, params (uint Id, uint Modes, byte Component)[] checks)
    {
        yield return Event(ArmingSummary, Protocol, (byte)0, error, 0u, canArm, canRun);
        foreach (var (id, modes, component) in checks)
        {
            yield return Event(id, Error, modes, component);
        }

        yield return Event(HealthSummary, Protocol, (byte)0, uint.MaxValue, error, 0u);
    }

    public static void SendReport(FakeVehicle vehicle, IEnumerable<VehicleEvent> report)
    {
        foreach (var @event in report)
        {
            vehicle.SendEvent(@event.Id, @event.LogLevels, @event.Arguments);
        }
    }

    /// <summary>Serves the events metadata over MAVLink FTP, as PX4 does.</summary>
    public static void Serve(FakeVehicle vehicle)
    {
        vehicle.ComponentMetadata = ("mftp://etc/general.json", 11);
        vehicle.Files["/etc/general.json"] = Encoding.UTF8.GetBytes("""{"version":1,"metadataTypes":[{"type":4,"uri":"mftp://etc/events.json","fileCrc":0}]}""");
        vehicle.Files["/etc/events.json"] = Encoding.UTF8.GetBytes(Json);
    }
}
