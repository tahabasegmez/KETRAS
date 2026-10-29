using Ketras.Mavlink.Messages;

namespace Ketras.Mavlink.Framing;

/// <summary>
/// Stream parser: turns arbitrary byte chunks into MAVLink 1 and 2 packets. A frame is accepted only
/// when its CRC (with the message's CRC_EXTRA) matches; on a mismatch one byte is skipped and the
/// search for the next start byte resumes. Signatures are skipped, not verified. Not thread-safe.
/// </summary>
internal sealed class MavlinkParser
{
    private byte[] _buffer = new byte[4096];
    private int _count;

    /// <summary>Frames that decoded to a known message.</summary>
    public long PacketsReceived { get; private set; }

    /// <summary>Frames whose checksum did not match (corruption or an unknown dialect).</summary>
    public long ChecksumErrors { get; private set; }

    /// <summary>Well-formed frames whose message id this build does not know; dropped.</summary>
    public long UnknownMessages { get; private set; }

    /// <summary>Adds received bytes and returns every packet completed by them; partial frames are kept for the next call.</summary>
    public IReadOnlyList<MavlinkPacket> Feed(ReadOnlySpan<byte> data)
    {
        Append(data);

        var packets = new List<MavlinkPacket>();
        var start = 0;

        while (true)
        {
            var stx = IndexOfStart(start);
            if (stx < 0)
            {
                start = _count;
                break;
            }

            start = stx;
            var result = TryDecode(start, out var consumed, out var packet);
            if (result == Decode.NeedMore)
            {
                break;
            }

            if (result == Decode.Invalid)
            {
                start++;
                continue;
            }

            if (packet is not null)
            {
                packets.Add(packet);
            }

            start += consumed;
        }

        Compact(start);
        return packets;
    }

    private enum Decode { NeedMore, Invalid, Ok }

    private Decode TryDecode(int at, out int consumed, out MavlinkPacket? packet)
    {
        consumed = 0;
        packet = null;
        var available = _count - at;
        var v2 = _buffer[at] == MavlinkFrame.StxV2;
        var header = v2 ? MavlinkFrame.HeaderLengthV2 : MavlinkFrame.HeaderLengthV1;

        if (available < header)
        {
            return Decode.NeedMore;
        }

        var span = _buffer.AsSpan(at, available);
        int length = span[1];
        var signed = false;
        byte sequence, systemId, componentId;
        uint messageId;

        if (v2)
        {
            var incompat = span[2];
            if ((incompat & ~MavlinkFrame.IncompatSigned) != 0)
            {
                return Decode.Invalid;
            }

            signed = (incompat & MavlinkFrame.IncompatSigned) != 0;
            sequence = span[4];
            systemId = span[5];
            componentId = span[6];
            messageId = (uint)(span[7] | (span[8] << 8) | (span[9] << 16));
        }
        else
        {
            sequence = span[2];
            systemId = span[3];
            componentId = span[4];
            messageId = span[5];
        }

        // Judged from the header alone, so a stray start byte with a large length never stalls the stream.
        if (!MavlinkRegistry.TryGet(messageId, out var info))
        {
            // No CRC_EXTRA to check against: the frame cannot be validated, so only the start byte is dropped.
            UnknownMessages++;
            return Decode.Invalid;
        }

        if (length > info.MaxLength)
        {
            return Decode.Invalid;
        }

        var total = header + length + MavlinkFrame.ChecksumLength + (signed ? MavlinkFrame.SignatureLength : 0);
        if (available < total)
        {
            return Decode.NeedMore;
        }

        var crc = MavlinkCrc.Accumulate(span.Slice(1, header - 1 + length), MavlinkCrc.Seed);
        crc = MavlinkCrc.Accumulate(info.CrcExtra, crc);
        var received = (ushort)(span[header + length] | (span[header + length + 1] << 8));
        if (crc != received)
        {
            ChecksumErrors++;
            return Decode.Invalid;
        }

        var message = info.Parse(span.Slice(header, length));
        packet = new MavlinkPacket(v2 ? 2 : 1, sequence, systemId, componentId, messageId, message, span[..total].ToArray());
        PacketsReceived++;
        consumed = total;
        return Decode.Ok;
    }

    private int IndexOfStart(int from)
    {
        for (var i = from; i < _count; i++)
        {
            if (_buffer[i] == MavlinkFrame.StxV2 || _buffer[i] == MavlinkFrame.StxV1)
            {
                return i;
            }
        }

        return -1;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_count + data.Length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _count + data.Length));
        }

        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;
    }

    private void Compact(int consumed)
    {
        if (consumed <= 0)
        {
            return;
        }

        _buffer.AsSpan(consumed, _count - consumed).CopyTo(_buffer);
        _count -= consumed;
    }
}
