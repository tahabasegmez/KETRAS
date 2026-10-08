using Ketras.Mavlink.Messages;

namespace Ketras.Mavlink.Framing;

/// <summary>One decoded packet: who sent it, in which order, the message, and the frame bytes as received.</summary>
internal sealed record MavlinkPacket(int Version, byte Sequence, byte SystemId, byte ComponentId, uint MessageId, IMavlinkMessage Message, byte[] Frame)
{
    /// <summary>True when the frame carries a MAVLink 2 signature block.</summary>
    public bool IsSigned => Version == 2 && (Frame[2] & MavlinkFrame.IncompatSigned) != 0;
}

/// <summary>MAVLink framing constants, the MAVLink 2 encoder and frame utilities.</summary>
internal static class MavlinkFrame
{
    /// <summary>MAVLink 2 start byte.</summary>
    public const byte StxV2 = 0xFD;

    /// <summary>MAVLink 1 start byte.</summary>
    public const byte StxV1 = 0xFE;

    /// <summary>MAVLink 2 header length, start byte included.</summary>
    public const int HeaderLengthV2 = 10;

    /// <summary>MAVLink 1 header length, start byte included.</summary>
    public const int HeaderLengthV1 = 6;

    /// <summary>Checksum length.</summary>
    public const int ChecksumLength = 2;

    /// <summary>Length of a MAVLink 2 signature block.</summary>
    public const int SignatureLength = 13;

    /// <summary>MAVLink 2 incompatibility flag: the frame is signed.</summary>
    public const byte IncompatSigned = 0x01;

    /// <summary>Largest payload a frame can carry.</summary>
    public const int MaxPayloadLength = 255;

    /// <summary>
    /// Encodes a MAVLink 2 frame. Trailing zero bytes of the payload are dropped, as MAVLink 2 requires,
    /// keeping at least the first byte.
    /// </summary>
    public static byte[] Encode(IMavlinkMessage message, byte sequence, byte systemId, byte componentId)
    {
        Span<byte> payload = stackalloc byte[MaxPayloadLength];
        var length = message.Serialize(payload);
        while (length > 1 && payload[length - 1] == 0)
        {
            length--;
        }

        var id = message.WireId;
        var frame = new byte[HeaderLengthV2 + length + ChecksumLength];
        frame[0] = StxV2;
        frame[1] = (byte)length;
        frame[2] = 0;
        frame[3] = 0;
        frame[4] = sequence;
        frame[5] = systemId;
        frame[6] = componentId;
        frame[7] = (byte)id;
        frame[8] = (byte)(id >> 8);
        frame[9] = (byte)(id >> 16);
        payload[..length].CopyTo(frame.AsSpan(HeaderLengthV2));
        WriteChecksum(frame, length, message.WireCrcExtra);
        return frame;
    }

    /// <summary>
    /// The frame without its signature block: the signed flag cleared and the checksum recomputed, as the MAVLink
    /// logging rules require. Unsigned frames are returned as they are.
    /// </summary>
    public static byte[] Unsigned(MavlinkPacket packet)
    {
        if (!packet.IsSigned)
        {
            return packet.Frame;
        }

        int length = packet.Frame[1];
        var frame = packet.Frame.AsSpan(0, HeaderLengthV2 + length + ChecksumLength).ToArray();
        frame[2] &= unchecked((byte)~IncompatSigned);
        WriteChecksum(frame, length, MavlinkRegistry.TryGet(packet.MessageId, out var info) ? info.CrcExtra : (byte)0);
        return frame;
    }

    private static void WriteChecksum(byte[] frame, int length, byte crcExtra)
    {
        var crc = MavlinkCrc.Accumulate(frame.AsSpan(1, HeaderLengthV2 - 1 + length), MavlinkCrc.Seed);
        crc = MavlinkCrc.Accumulate(crcExtra, crc);
        frame[HeaderLengthV2 + length] = (byte)crc;
        frame[HeaderLengthV2 + length + 1] = (byte)(crc >> 8);
    }
}
