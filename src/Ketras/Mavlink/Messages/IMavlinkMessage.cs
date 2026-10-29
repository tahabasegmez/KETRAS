namespace Ketras.Mavlink.Messages;

/// <summary>A MAVLink message that can be written to the wire. Implemented by every generated message class.</summary>
internal interface IMavlinkMessage
{
    /// <summary>Message id.</summary>
    uint WireId { get; }

    /// <summary>CRC_EXTRA seed of this message's definition.</summary>
    byte WireCrcExtra { get; }

    /// <summary>Payload length with every field, extensions included.</summary>
    int WireMaxLength { get; }

    /// <summary>Writes the full payload in wire order; returns <see cref="WireMaxLength"/>.</summary>
    int Serialize(Span<byte> buffer);
}

/// <summary>Decodes a payload (possibly truncated) into a message.</summary>
internal delegate IMavlinkMessage MavlinkMessageParser(ReadOnlySpan<byte> payload);

/// <summary>What the parser needs to know about one message id.</summary>
internal sealed record MavlinkMessageInfo(string Name, byte CrcExtra, int MinLength, int MaxLength, MavlinkMessageParser Parse);

/// <summary>Registry: message id to definition, filled by the generated table.</summary>
internal static partial class MavlinkRegistry
{
    /// <summary>Definition of a message id, if this build knows it.</summary>
    public static bool TryGet(uint messageId, out MavlinkMessageInfo info) => Table.TryGetValue(messageId, out info!);

    /// <summary>Number of known messages.</summary>
    public static int Count => Table.Count;
}
