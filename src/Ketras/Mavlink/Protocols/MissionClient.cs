using System.Threading.Channels;
using Ketras.Mavlink.Messages;

namespace Ketras.Mavlink.Protocols;

/// <summary>
/// One mission, fence or rally item in MISSION_ITEM_INT form. <see cref="X"/>/<see cref="Y"/> are degrees × 1e7
/// for global frames; NaN params mean "not set" and survive the transfer unchanged.
/// </summary>
public sealed record MissionItem(
    MavFrame Frame,
    MavCmd Command,
    bool AutoContinue,
    float Param1,
    float Param2,
    float Param3,
    float Param4,
    int X,
    int Y,
    float Z)
{
    /// <summary>Item at a WGS84 position (degrees), altitude in the frame's reference.</summary>
    public static MissionItem At(MavFrame frame, MavCmd command, double latitude, double longitude, float altitude,
        float param1 = 0, float param2 = 0, float param3 = 0, float param4 = 0, bool autoContinue = true) =>
        new(frame, command, autoContinue, param1, param2, param3, param4,
            (int)Math.Round(latitude * 1e7), (int)Math.Round(longitude * 1e7), altitude);

    /// <summary>Item without a position (MAV_FRAME_MISSION, x/y/z zero).</summary>
    public static MissionItem Action(MavCmd command, float param1 = 0, float param2 = 0, float param3 = 0, float param4 = 0, bool autoContinue = true) =>
        new(MavFrame.Mission, command, autoContinue, param1, param2, param3, param4, 0, 0, 0);
}

/// <summary>Outcome of a transfer. <see cref="Items"/> is set for downloads.</summary>
public sealed record MissionTransferResult(bool IsSuccess, MavMissionResult? Result, string Reason, IReadOnlyList<MissionItem>? Items = null);

/// <summary>
/// MAVLink mission protocol for MISSION, FENCE and RALLY lists: upload, download, clear. One transfer at a
/// time; each step is retried after 1.5 s up to 5 times. The vehicle validates the
/// items; its MAV_MISSION_RESULT is returned as is. Sources in docs/reference.
/// </summary>
public sealed class MissionClient
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMilliseconds(1500);
    private const int MaxRetries = 5;

    private readonly Func<IMavlinkMessage, ValueTask> _send;
    private readonly byte _targetSystem;
    private readonly byte _targetComponent;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Channel<IMavlinkMessage>? _inbox;

    internal MissionClient(Func<IMavlinkMessage, ValueTask> send, byte targetSystem, byte targetComponent)
    {
        _send = send;
        _targetSystem = targetSystem;
        _targetComponent = targetComponent;
    }

    /// <summary>Replaces the vehicle's list of this type. An empty list clears it.</summary>
    public async Task<MissionTransferResult> UploadAsync(MavMissionType type, IReadOnlyList<MissionItem> items, CancellationToken cancellation = default)
    {
        return await TransferAsync(async inbox =>
        {
            IMavlinkMessage last = new MissionCountMessage
            {
                TargetSystem = _targetSystem,
                TargetComponent = _targetComponent,
                Count = (ushort)items.Count,
                MissionType = (byte)type
            };
            await _send(last);

            var retries = 0;
            while (true)
            {
                var message = await NextAsync(inbox, cancellation);
                if (message is null)
                {
                    if (++retries > MaxRetries)
                    {
                        return Failure("no response from the vehicle");
                    }

                    await _send(last);
                    continue;
                }

                int requested;
                switch (message)
                {
                    case MissionRequestIntMessage r when r.MissionType == (byte)type:
                        requested = r.Seq;
                        break;
                    case MissionRequestMessage r when r.MissionType == (byte)type:
                        requested = r.Seq;
                        break;
                    case MissionAckMessage ack when ack.MissionType == (byte)type:
                        return Result((MavMissionResult)ack.Type);
                    default:
                        continue;
                }

                if (requested >= items.Count)
                {
                    return Failure($"the vehicle requested item {requested} of {items.Count}");
                }

                last = ItemMessage(type, requested, items);
                retries = 0;
                await _send(last);
            }
        }, cancellation);
    }

    /// <summary>Reads the vehicle's list of this type.</summary>
    public async Task<MissionTransferResult> DownloadAsync(MavMissionType type, CancellationToken cancellation = default)
    {
        return await TransferAsync(async inbox =>
        {
            var count = -1;
            var list = new MissionRequestListMessage { TargetSystem = _targetSystem, TargetComponent = _targetComponent, MissionType = (byte)type };
            for (var retries = 0; count < 0; retries++)
            {
                if (retries > MaxRetries)
                {
                    return Failure("no MISSION_COUNT from the vehicle");
                }

                await _send(list);
                if (await NextAsync(inbox, cancellation, m => m is MissionCountMessage c && c.MissionType == (byte)type) is MissionCountMessage countMessage)
                {
                    count = countMessage.Count;
                }
            }

            var items = new List<MissionItem>(count);
            for (var seq = 0; seq < count; seq++)
            {
                MissionItem? item = null;
                var request = new MissionRequestIntMessage
                {
                    TargetSystem = _targetSystem,
                    TargetComponent = _targetComponent,
                    Seq = (ushort)seq,
                    MissionType = (byte)type
                };

                for (var retries = 0; item is null; retries++)
                {
                    if (retries > MaxRetries)
                    {
                        return Failure($"no item {seq} from the vehicle");
                    }

                    await _send(request);
                    var expected = seq;
                    item = await NextAsync(inbox, cancellation, m => IsItem(m, type, expected)) switch
                    {
                        MissionItemIntMessage i => FromInt(i),
                        MissionItemMessage f => FromFloat(f),
                        _ => null
                    };
                }

                items.Add(item);
            }

            await _send(new MissionAckMessage
            {
                TargetSystem = _targetSystem,
                TargetComponent = _targetComponent,
                Type = (byte)MavMissionResult.MavMissionAccepted,
                MissionType = (byte)type
            });

            return new MissionTransferResult(true, MavMissionResult.MavMissionAccepted, string.Empty, items);
        }, cancellation);
    }

    /// <summary>Clears the vehicle's list of this type.</summary>
    public async Task<MissionTransferResult> ClearAsync(MavMissionType type, CancellationToken cancellation = default)
    {
        return await TransferAsync(async inbox =>
        {
            var clear = new MissionClearAllMessage { TargetSystem = _targetSystem, TargetComponent = _targetComponent, MissionType = (byte)type };
            for (var retries = 0; retries <= MaxRetries; retries++)
            {
                await _send(clear);
                if (await NextAsync(inbox, cancellation, m => m is MissionAckMessage a && a.MissionType == (byte)type) is MissionAckMessage ack)
                {
                    return Result((MavMissionResult)ack.Type);
                }
            }

            return Failure("no response from the vehicle");
        }, cancellation);
    }

    internal void Handle(IMavlinkMessage message)
    {
        if (message is MissionRequestIntMessage or MissionRequestMessage or MissionAckMessage or MissionCountMessage
            or MissionItemIntMessage or MissionItemMessage)
        {
            _inbox?.Writer.TryWrite(message);
        }
    }

    private async Task<MissionTransferResult> TransferAsync(Func<Channel<IMavlinkMessage>, Task<MissionTransferResult>> transfer, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        var inbox = Channel.CreateUnbounded<IMavlinkMessage>();
        _inbox = inbox;
        try
        {
            return await transfer(inbox);
        }
        finally
        {
            _inbox = null;
            _gate.Release();
        }
    }

    /// <summary>Next message matching <paramref name="accept"/> within the step timeout; null on timeout.</summary>
    private static async Task<IMavlinkMessage?> NextAsync(Channel<IMavlinkMessage> inbox, CancellationToken cancellation, Func<IMavlinkMessage, bool>? accept = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(StepTimeout);
        try
        {
            while (true)
            {
                var message = await inbox.Reader.ReadAsync(timeout.Token);
                if (accept is null || accept(message))
                {
                    return message;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    private MissionItemIntMessage ItemMessage(MavMissionType type, int seq, IReadOnlyList<MissionItem> items)
    {
        var item = items[seq];
        return new MissionItemIntMessage
        {
            TargetSystem = _targetSystem,
            TargetComponent = _targetComponent,
            Seq = (ushort)seq,
            Frame = (byte)item.Frame,
            Command = (ushort)item.Command,
            Current = (byte)(seq == 0 ? 1 : 0),
            Autocontinue = (byte)(item.AutoContinue ? 1 : 0),
            Param1 = item.Param1,
            Param2 = item.Param2,
            Param3 = item.Param3,
            Param4 = item.Param4,
            X = item.X,
            Y = item.Y,
            Z = item.Z,
            MissionType = (byte)type
        };
    }

    private static bool IsItem(IMavlinkMessage message, MavMissionType type, int seq) => message switch
    {
        MissionItemIntMessage i => i.Seq == seq && i.MissionType == (byte)type,
        MissionItemMessage f => f.Seq == seq && f.MissionType == (byte)type,
        _ => false
    };

    private static MissionItem FromInt(MissionItemIntMessage i) =>
        new((MavFrame)i.Frame, (MavCmd)i.Command, i.Autocontinue != 0, i.Param1, i.Param2, i.Param3, i.Param4, i.X, i.Y, i.Z);

    private static MissionItem FromFloat(MissionItemMessage f) =>
        new((MavFrame)f.Frame, (MavCmd)f.Command, f.Autocontinue != 0, f.Param1, f.Param2, f.Param3, f.Param4,
            (int)Math.Round(f.X * 1e7), (int)Math.Round(f.Y * 1e7), f.Z);

    private static MissionTransferResult Result(MavMissionResult result) =>
        new(result == MavMissionResult.MavMissionAccepted, result,
            result == MavMissionResult.MavMissionAccepted ? string.Empty : result.ToString());

    private static MissionTransferResult Failure(string reason) => new(false, null, reason);
}
