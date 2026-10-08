using System.Text.Json;
using Ketras.Mavlink.Framing;
using Ketras.Mavlink.Messages;

namespace Ketras.Mavlink.Tests;

public sealed class FramingTests
{
    private static readonly float PythonNaN = BitConverter.Int32BitsToSingle(0x7FC00000);

    public static TheoryData<string> FixtureNames() => ["HEARTBEAT", "COMMAND_LONG", "COMMAND_LONG_ZEROS", "MISSION_ITEM_INT", "PARAM_SET", "STATUSTEXT", "FILE_TRANSFER_PROTOCOL"];

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Encoder_matches_pymavlink_byte_for_byte(string name)
    {
        var expected = Convert.FromHexString(Fixture(name));
        var actual = MavlinkFrame.Encode(Message(name), 7, 255, 190);
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Parser_decodes_pymavlink_frames_fed_one_byte_at_a_time_behind_garbage(string name)
    {
        var frame = Convert.FromHexString(Fixture(name));
        var parser = new MavlinkParser();
        var packets = new List<MavlinkPacket>();
        foreach (var b in new byte[] { 0x00, 0xFD, 0xFE, 0x42 }.Concat(frame))
        {
            packets.AddRange(parser.Feed([b]));
        }

        var packet = Assert.Single(packets);
        Assert.Equal(Message(name).WireId, packet.MessageId);
        Assert.Equal(frame, packet.Frame);
    }

    [Fact]
    public void A_corrupted_checksum_drops_the_frame_and_the_next_one_still_decodes()
    {
        var bad = MavlinkFrame.Encode(new HeartbeatMessage { Type = 1 }, 1, 1, 1);
        bad[^1] ^= 0xFF;
        var good = MavlinkFrame.Encode(new HeartbeatMessage { Type = 2 }, 2, 1, 1);
        var parser = new MavlinkParser();

        var packets = parser.Feed(bad.Concat(good).ToArray());

        var packet = Assert.Single(packets);
        Assert.Equal(2, ((HeartbeatMessage)packet.Message).Type);
        Assert.Equal(1, parser.ChecksumErrors);
    }

    [Fact]
    public void A_signed_frame_is_logged_without_its_signature_and_with_a_valid_checksum()
    {
        var unsigned = MavlinkFrame.Encode(new HeartbeatMessage { Type = 1, MavlinkVersion = 3 }, 5, 1, 1);
        var signed = unsigned.ToArray();
        signed[2] |= MavlinkFrame.IncompatSigned;
        var crc = MavlinkCrc.Accumulate(signed.AsSpan(1, MavlinkFrame.HeaderLengthV2 - 1 + signed[1]), MavlinkCrc.Seed);
        crc = MavlinkCrc.Accumulate(HeartbeatMessage.WireCrcExtra, crc);
        signed[^2] = (byte)crc;
        signed[^1] = (byte)(crc >> 8);
        signed = [.. signed, .. new byte[MavlinkFrame.SignatureLength]];

        var packet = Assert.Single(new MavlinkParser().Feed(signed));

        Assert.True(packet.IsSigned);
        Assert.Equal(unsigned, MavlinkFrame.Unsigned(packet));
    }

    private static string Fixture(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pymavlink-frames.json")));
        return document.RootElement.EnumerateArray().Single(e => e.GetProperty("name").GetString() == name).GetProperty("hex").GetString()!;
    }

    private static IMavlinkMessage Message(string name) => name switch
    {
        "HEARTBEAT" => new HeartbeatMessage { Type = 6, Autopilot = 8, BaseMode = 0, CustomMode = 0, SystemStatus = 4, MavlinkVersion = 3 },
        "COMMAND_LONG" => new CommandLongMessage
        {
            TargetSystem = 1, TargetComponent = 1, Command = 22, Confirmation = 0,
            Param1 = PythonNaN, Param2 = 0, Param3 = 0, Param4 = PythonNaN, Param5 = PythonNaN, Param6 = PythonNaN, Param7 = 120.5f
        },
        "COMMAND_LONG_ZEROS" => new CommandLongMessage { TargetSystem = 1, TargetComponent = 1, Command = 400, Param1 = 1 },
        "MISSION_ITEM_INT" => new MissionItemIntMessage
        {
            TargetSystem = 1, TargetComponent = 1, Seq = 3, Frame = 6, Command = 16, Current = 0, Autocontinue = 1,
            Param1 = 0, Param2 = 0, Param3 = 0, Param4 = PythonNaN, X = 473977418, Y = 85455939, Z = 100, MissionType = 0
        },
        "PARAM_SET" => new ParamSetMessage { TargetSystem = 1, TargetComponent = 1, ParamId = "NAV_LOITER_RAD", ParamValue = 80, ParamType = 9 },
        "STATUSTEXT" => new StatustextMessage { Severity = 4, Text = "Preflight check failed" },
        "FILE_TRANSFER_PROTOCOL" => new FileTransferProtocolMessage
        {
            TargetNetwork = 0, TargetSystem = 1, TargetComponent = 1, Payload = Enumerable.Range(0, 251).Select(i => (byte)i).ToArray()
        },
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
}
