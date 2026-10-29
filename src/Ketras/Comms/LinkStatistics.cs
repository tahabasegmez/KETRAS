namespace Ketras.Comms;

/// <summary>
/// Counters of one link since it opened. Loss comes from gaps in each sender's MAVLink sequence numbers, the same way
/// </summary>
/// <param name="BytesReceived">Bytes delivered by the transport.</param>
/// <param name="PacketsReceived">Frames that decoded to a known message.</param>
/// <param name="PacketsLost">Frames missing from the senders' sequence numbers.</param>
/// <param name="ChecksumErrors">Frames dropped for a bad checksum.</param>
/// <param name="Radio">Latest telemetry radio report, if any.</param>
/// <param name="PacketsDropped">Frames this station dropped because its subscribers fell behind.</param>
/// <param name="SendErrors">Sends the transport refused.</param>
public sealed record LinkStatistics(
    long BytesReceived,
    long PacketsReceived,
    long PacketsLost,
    long ChecksumErrors,
    RadioReport? Radio,
    long PacketsDropped = 0,
    long SendErrors = 0)
{
    /// <summary>Lost share of all packets sent to this station, 0…1; null before any packet.</summary>
    public double? LossRatio => PacketsReceived + PacketsLost == 0 ? null : (double)PacketsLost / (PacketsReceived + PacketsLost);
}

/// <summary>
/// Latest RADIO_STATUS (#109), as sent by a telemetry radio such as a SiK modem. RSSI and noise are device-dependent
/// units (about 2× dB on SiK radios); 255 means unknown.
/// </summary>
/// <param name="Rssi">Local signal strength.</param>
/// <param name="RemoteRssi">Remote signal strength.</param>
/// <param name="Noise">Local background noise.</param>
/// <param name="RemoteNoise">Remote background noise.</param>
/// <param name="TxBufferPercent">Remaining free transmit buffer space.</param>
/// <param name="RxErrors">Receive errors.</param>
/// <param name="Fixed">Packets corrected by error correction.</param>
/// <param name="ReceivedUtc">When this report arrived.</param>
public sealed record RadioReport(
    byte Rssi,
    byte RemoteRssi,
    byte Noise,
    byte RemoteNoise,
    byte TxBufferPercent,
    ushort RxErrors,
    ushort Fixed,
    DateTime ReceivedUtc);
