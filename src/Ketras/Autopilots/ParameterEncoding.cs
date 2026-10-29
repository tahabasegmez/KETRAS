using System.Buffers.Binary;
using Ketras.Mavlink.Messages;

namespace Ketras.Autopilots;

/// <summary>The two ways autopilots put parameter values into MAVLink's float field.</summary>
internal static class ParameterEncoding
{
    /// <summary>
    /// MAV_PROTOCOL_CAPABILITY_PARAM_ENCODE_BYTEWISE: integers are stored bit-for-bit in the four bytes
    /// of the float, little-endian, in the type's own width.
    /// </summary>
    public static float EncodeBytewise(ParameterValue value)
    {
        if (!value.IsInteger)
        {
            return (float)value.Value;
        }

        Span<byte> bytes = stackalloc byte[4];
        bytes.Clear();
        var whole = (long)Math.Round(value.Value);
        switch (value.Type)
        {
            case MavParamType.Uint8:
            case MavParamType.Int8:
                bytes[0] = (byte)whole;
                break;
            case MavParamType.Uint16:
            case MavParamType.Int16:
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)whole);
                break;
            default:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)whole);
                break;
        }

        return BinaryPrimitives.ReadSingleLittleEndian(bytes);
    }

    /// <summary>Reverse of <see cref="EncodeBytewise"/>.</summary>
    public static ParameterValue DecodeBytewise(float raw, MavParamType type)
    {
        if (type is MavParamType.Real32 or MavParamType.Real64)
        {
            return new ParameterValue(type, raw);
        }

        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, raw);
        double value = type switch
        {
            MavParamType.Uint8 => bytes[0],
            MavParamType.Int8 => (sbyte)bytes[0],
            MavParamType.Uint16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            MavParamType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
            MavParamType.Uint32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            _ => BinaryPrimitives.ReadInt32LittleEndian(bytes)
        };
        return new ParameterValue(type, value);
    }

    /// <summary>MAV_PROTOCOL_CAPABILITY_PARAM_ENCODE_C_CAST: the value is simply converted to float.</summary>
    public static float EncodeCast(ParameterValue value) => (float)value.Value;

    /// <summary>Reverse of <see cref="EncodeCast"/>.</summary>
    public static ParameterValue DecodeCast(float raw, MavParamType type) => new(type, raw);
}
