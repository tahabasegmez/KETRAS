namespace Ketras.Mavlink.Framing;

/// <summary>CRC-16/MCRF4XX (X.25) as MAVLink uses it: seed 0xFFFF, over header and payload, then CRC_EXTRA.</summary>
internal static class MavlinkCrc
{
    /// <summary>Initial CRC value.</summary>
    public const ushort Seed = 0xFFFF;

    /// <summary>Adds one byte to a running CRC.</summary>
    public static ushort Accumulate(byte data, ushort crc)
    {
        var tmp = (byte)(data ^ (byte)(crc & 0xFF));
        tmp ^= (byte)(tmp << 4);
        return (ushort)((crc >> 8) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4));
    }

    /// <summary>Adds a byte span to a running CRC.</summary>
    public static ushort Accumulate(ReadOnlySpan<byte> data, ushort crc)
    {
        foreach (var b in data)
        {
            crc = Accumulate(b, crc);
        }

        return crc;
    }
}
